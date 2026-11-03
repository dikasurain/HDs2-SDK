/* ============================================================
 * Mic_Sdk.c —— 下位机通讯 SDK 缩小版（自包含单文件，不依赖 sdk_lower）
 * ------------------------------------------------------------
 * 由 sdk_lower.c（完整重写版）精简而来的缩小版：
 *   指令保留 0x01 转速 / 0x02 启动 / 0x03 停止 / 0x04 方向（运行中
 *            拒改防瞬间反向，静止可改）/ 0x0A 按键信号
 *            （写 TBAJ/ZXAJ/GDQHAJ 按键设置位），
 *   应答保留 0x80 ACK / 0x81 心跳反馈：
 *     ACK 结果字节 = 对应指令所写旗帜位的当前值（直接镜像：
 *       0x01→SetRpm_flag 0x02→SoftwareRun_flag 0x03→MotorStop
 *       0x04→MotorDir(1=正转 2=反转) 0x0A→按键位电平）；
 *     0x81 心跳变长省流量（停止 2 字节 = 软件运行位+报警（ERR 三态），
 *     运行中(MotorRun=1) 7 字节 = 软件运行位+转速(3)+电流(2)+报警）；
 *   0x05 电流 / 0x06 供电 / 0x07 握手 / 0x08/09 转速加减已裁剪
 *   （缩小版收到不执行，回 ACK 结果=0——无对应旗帜位可镜像）
 *
 * 协议（简化版）：起始位(0x2F) + CMD + LEN + DATA（无 CRC，帧总长=LEN+3）
 *   （诊断期帧尾临时追加 0x55 0xAA 两字节定位吞尾问题，见 frame_flush）
 *
 * 结构体输入模式（sdk 为两层公共数据中心）：
 *   下行：上位机指令/按键 → 触发处直接编辑 sdk 对应设置位
 *         （启动位/停止位/方向/转速/档位参数），结构体即后端控制
 *   输出：Mic_Sdk_Exec 仅把已编辑的状态镜像到硬件（不做裁决）
 *   上行：实时数据（Rpm/电流/故障）由下位机采集层直接写入 sdk
 *         → heart_feedback 读走上报（本模块不负责采集）
 *
 * 接收模型 —— 中断生产/主循环消费（经典单生产者单消费者，免锁）：
 *   ① 数组更新：串口中断存字节入环形缓冲——中断只写 rx_w、主循环
 *      只写 rx_r，各动各的指针，天然无竞态无需关中断
 *   ② 主循环消费：rx_poll 有数据即解析（不等攒满）——帧攒齐立即
 *      处理（单帧指令 ACK 闭环下一圈即应答）；帧未攒齐=在途，
 *      继续等（每收 1 字节重装空闲计数）；长时间无新字节仍不齐
 *      =假帧头卡死，空闲计数耗尽后逐字节清障重找
 *   ③ 拆分任务：逐帧"找帧头 → 按 LEN 算帧长 → 攒齐即解析（无 CRC）→
 *      直接编辑旗帜位 + 回 ACK（结果字节=旗帜位当前值镜像）"
 *   ④ 满缓冲策略：丢新字节保护已攒数据（连续消费下几乎不会满）
 * ============================================================ */
#include "Mic_Sdk.h"
#include "motor.h"


/* ---------- 协议常量 ---------- */
#define FRAME_HEAD   0x2Fu   /* 起始位 */
#define RX_DATA_MAX  60u     /* DATA 最大字节数（当前指令最多 4：0x01 转速 32 位） */
#define RX_BUF_SIZE  128u    /* 环形缓冲容量（2 的幂，掩码取模） */
#define RPM_MAX      99999u  /* 转速参数上限根据手柄情况修改填入 */
#define RPM_BYTES    3u      /* 心跳上报转速字节长度（3 字节大端；0x01 指令为 4 字节直接赋值） */
#define CMD_ACK      0x80u   /* 指令确认应答码 */
#define CMD_FEEDBACK 0x81u   /* 综合反馈指令码 */
#define CMD_QUERY_SETRPM 0x0Bu  /* 查询 SetRpm：ACK 附带当前 SetRpm（4 字节大端），不修改任何状态 */
#define RX_IDLE_POLLS 20u    /* 假帧头清障圈数：帧未攒齐且连续 N 圈无新字节
                                  * 判定卡死，丢队首字节重找（不依赖毫秒时基）；
                                  *   在途帧不受影响——每收 1 字节重装计数，
                                  *   N=20 配合主循环阻塞发送(≥1ms/圈)远大于
                                  *   字节间隔 87µs@115200，不会误清在途帧 */
#define HEART_DIV     8u     /* 心跳分频：每 N 圈主循环发一帧 0x81——
                                  * 阻塞发送本身约 0.8~1.2ms/帧，不分频会
                                  * 打满总线（800~1600 帧/秒）刷爆上位机；
                                  * N=8 ≈10ms 周期（≈100 帧/秒），保活与
                                  * 实时性兼顾；需精确定期改硬件定时器调
                                  * heart_feedback() */

/* ---------- 高低速档位参数（GDQHAJ 按下沿切换） ----------
 * TODO: 按实际电机规格/负载情况填入（转速 ≤ RPM_MAX=99999，电流 mA）
 * 上位机高低速控制接口：0x0A 按键信号帧 data[0]=3 data[1]=1 触发切换；
 * 未填值（宏全 0）时 gear_toggle 不改写参数（防切档归零转速） */
#define GEAR_LO_RPM  0u      /* 低速档转速 */
#define GEAR_HI_RPM  0u      /* 高速档转速 */
#define GEAR_LO_MA   0u      /* 低速档电流限制 mA */
#define GEAR_HI_MA   0u      /* 高速档电流限制 mA */

/* ---------- 全局唯一实例 ---------- */
_Sdk sdk;

/* ============================================================
 * 串口发送单字节 —— 按你的 MCU 在此实现（唯一对接点）
 * ============================================================ */
static void uart_send_byte(uint8_t b)
{
	while(USART_GetFlagStatus(USART3, USART_FLAG_TC) == RESET);	  
	USART_SendData(USART3,b);
}

/* ============================================================
 * 状态编辑函数前向声明（定义在后文，key_event 需提前调用） */
static void run_on(void);      /* 启动位编辑 */
static void run_off(void);     /* 停止位编辑 */
static void gear_toggle(void); /* 档位参数编辑 */

/* ============================================================
 * 按键事件统一入口 —— 上位机 0x0A 模拟按键与实体按键共同路径：
 *   ① 功能处理：写按键信号位 + 直接编辑启停位（与 0x02/0x03 同一
 *      操作位置 MotorRun/MotorStop——按下=run_on 启动，松开=无其他
 *      运行源才 run_off 停止，脚踏优先）
 *   ② 参数处理：启动后 SetRpm 由 Exec 生效输出，停止后 Rpm 归零
 * ============================================================ */
void key_event_tba(uint8_t pressed)   /* 脚踏按键事件（0=松开 1=按下） */
{
    sdk.TBAJ = pressed;
    if (pressed) run_on();                        /* 功能：启动 */
    else if (!sdk.ZXAJ) run_off();                /* 功能：面板未按住才停 */
}

void key_event_zx(uint8_t pressed)    /* 面板按键事件（与脚踏同功能独立信号） */
{
    sdk.ZXAJ = pressed;
    if (pressed) run_on();
    else if (!sdk.TBAJ) run_off();                /* 脚踏未踩住才停 */
}

void key_event_gdq(uint8_t pressed)    /* 高低速按键事件（0=松开 1=按下） */
{
    /* ① 功能处理：按键信号位 + 按下沿（0→1）触发档位切换功能 */
    if (pressed && !sdk.GDQHAJ)
        gear_toggle();        /* ② 参数处理：档位参数成对编辑
                               * （SetRpm/SetElecricity + SetRpm_flag） */
    sdk.GDQHAJ = pressed;
}

/* ============================================================
 * （简化版·无 CRC）原整帧双重 CRC 校验已移除——
 * 帧可信度由"帧头+LEN 合法+攒齐"保证，噪声误判由
 * 空闲计数清障兜底（文本噪声无 0x2F 帧头，实际不构成威胁）
 * ============================================================ */

/* ============================================================
 * 环形缓冲 —— 中断生产（写），主循环消费（读），免锁绕圈覆盖
 * ============================================================ */
static uint8_t rx_buf[RX_BUF_SIZE];
static volatile uint8_t rx_w;     /* 写指针（中断写） */
static volatile uint8_t rx_r;     /* 读指针（主循环读） */
/* 空闲计数：中断每收 1 字节重装 RX_IDLE_POLLS；主循环仅在
 * "帧未攒齐"阻塞时递减——减到 0 仍无新字节 = 假帧头卡死，
 * 丢队首字节清障（在途帧因不断重装永不误清）。
 * 注：主循环的"--"与中断的重装存在读-改-写竞态窗口，但帧传输
 * 期间字节间隔 87µs ≪ 主循环一圈 ≥1ms，计数每圈被重装多次，
 * 偶尔丢失一次重装无实际影响（最坏多等 N 圈），不另加保护 */
static volatile uint8_t rx_idle_cnt;

/* 上电复位：缓冲清空 + sdk 全字段复位（含 MotorStop=1 非零初值）。
 * 注：若应用层已自行初始化 sdk（如恢复掉电前的状态），可跳过
 * 本函数中的 sdk 复位段，只保留缓冲清空三行 */
void rx_init(void)
{
    rx_w = 0;
    rx_r = 0;
    rx_idle_cnt = 0;

    sdk.SoftwareRun_flag = 0;    /* 0:未运行 */
    sdk.SetRpm_flag      = 0;    /* 0:未设置 */
    sdk.MotorRun         = 0;    /* 停止 */
    sdk.MotorStop        = 1;    /* 上电默认停止态 */
    sdk.MotorDir         = 1;    /* 默认正转 */
    sdk.MotorPower       = 0;    /* 默认断电 */
    sdk.TBAJ             = 0;    /* 脚踏松开（1=踩住请求运行） */
    sdk.ZXAJ             = 0;    /* 面板松开（1=按住请求运行） */
    sdk.GDQHAJ           = 0;    /* 高低速按键默认未按下 */
    sdk.SetRpm           = 0;    /* 默认显示设置位为0 */
    sdk.Rpm              = 0;    /* 实时数据由采集层直接写入，此处仅上电清零 */
    sdk.SetElecricity    = 0;
    sdk.Electricity      = 0;    /* 同上 */
    sdk.ERR              = 0;    /* 0:无错误（实时故障由采集层写入） */
}

void rx_handle_byte(uint8_t b)   /* 中断里调用：只存字节（耗时 < 1us） */
{
    uint8_t next = (uint8_t)((rx_w + 1) & (RX_BUF_SIZE - 1));
    if (next == rx_r)
        return;                   /* 满缓冲：丢新字节，保护已攒的未读数据 */
    rx_buf[rx_w] = b;
    rx_w = next;
    rx_idle_cnt = RX_IDLE_POLLS;   /* 重装：收到新字节，在途帧永不误清 */
}

static uint8_t rx_available(void) /* 未读字节数 */
{
    return (uint8_t)((rx_w - rx_r) & (RX_BUF_SIZE - 1));
}

static uint8_t rx_peek(uint8_t i) /* 看队首后第 i 字节（不出队） */
{
    return rx_buf[(rx_r + i) & (RX_BUF_SIZE - 1)];
}

static void rx_drop(uint8_t n)    /* 消费队首 n 字节（后移读指针） */
{
    rx_r = (uint8_t)((rx_r + n) & (RX_BUF_SIZE - 1));
}

/* ============================================================
 * 发送组帧（简化版·无 CRC）：head + cmd + len + data，逐字节发出
 * （ACK/反馈共用此出口）
 * ============================================================ */
static uint8_t tx_buf[3u + RX_DATA_MAX];

static void frame_flush(uint8_t cmd, const uint8_t *data, uint8_t len)
{
    uint8_t  i;

    tx_buf[0] = FRAME_HEAD;
    tx_buf[1] = cmd;
    tx_buf[2] = len;
    for (i = 0; i < len; i++)
        tx_buf[3 + i] = data[i];

    for (i = 0; i < (uint8_t)(3 + len); i++)
        uart_send_byte(tx_buf[i]);

    /* 诊断尾帧：帧后追加 0x55 + 0xAA（不在 LEN 内）
     * 0xAA 为牺牲填充：若 printf 抢占/覆盖发生在帧末，
     * 0xAA 优先被吃掉，0x55 的位置保持可判读
     * 观察帧尾两个字节即可定位（帧末字节以短心跳 ERR 为例，正常 X=ERR）：
     *   ... X 55 AA    → 帧完整（尾字节正常上线）
     *   ... X 0D AA    → 55 被覆盖（TDR 抢占写后移一位）
     *   ... X AA      → 55 整字节被吞（发送循环少发/跳过）
     *   ... 0D 55 AA  → 帧末字节被覆盖（TDR 抢占写在帧末）
     * 上位机解析器按帧头扫描，55/AA 无 0x2F 当噪声丢弃，协议不受影响
     * TODO: 诊断完成后删除这两行恢复无帧尾格式 */
    uart_send_byte(0x55);
    uart_send_byte(0xAA);
}

static void send_ack(uint8_t cmd, uint8_t result)  /* 指令确认应答 0x80 */
{
    if (cmd == CMD_QUERY_SETRPM) {
        /* 0x0B 查询 SetRpm：ACK 附带当前 SetRpm 实际存储值（4 字节大端）——
         * 上位机发送查询指令即可读取下位机确认写入的转速，不修改任何状态 */
        uint8_t d[2u + 4u];
        d[0] = cmd;                     /* 被确认指令码 */
        d[1] = result;                  /* 查询结果（1=查询成功） */
        d[2] = (uint8_t)((sdk.SetRpm >> 24) & 0xFFu);   /* SetRpm 高字节 */
        d[3] = (uint8_t)((sdk.SetRpm >> 16) & 0xFFu);
        d[4] = (uint8_t)((sdk.SetRpm >> 8) & 0xFFu);
        d[5] = (uint8_t)(sdk.SetRpm & 0xFFu);           /* SetRpm 低字节 */
        frame_flush(CMD_ACK, d, (uint8_t)(2u + 4u));
    } else {
        uint8_t d[2];
        d[0] = cmd;                     /* 被确认指令码 */
        d[1] = result;                  /* 对应指令所写旗帜位的当前值（直接镜像） */
        frame_flush(CMD_ACK, d, 2);
    }
}

/* 综合反馈/心跳 0x81 —— 按实际运行状态两种帧长（省流量设计）：
 *   停止（MotorRun=0，联机保持）短心跳 2 字节：
 *     d[0] SoftwareRun_flag（联机握手位：链路建立后恒 1，固件复位回 0）
 *     d[1] ERR 三态（0=正常 1=过载 2=故障——第一优先状态）
 *   运行中（MotorRun=1）全量 7 字节：
 *     d[0]   SoftwareRun_flag（联机握手位）
 *     d[1..3] Rpm 实时转速（3 字节大端）
 *     d[4..5] Electricity 电流（2 字节大端）
 *     d[6]   ERR 三态（0=正常 1=过载 2=故障——第一优先状态）
 * 帧长依据=MotorRun（电机实际运行），首位内容=SoftwareRun_flag
 * （联机标志）——两者职责分离：上位机联机状态看链路层，运行
 * 状态看帧长（长帧=运行中；短帧=停止）
 * ERR 三态（纯应用层数据源，协议层不写）：
 *   采集检测写入——电流超限写 1=过载；过温/堵转等写 2=故障；
 *   恢复正常写 0（指令执行与否由旗帜位本身表达，不再写入 ERR）
 * 处置归属：ERR≠0 时下位机不改变电机行为（本体保护由硬件电流
 *   限制承担），仅上报错误码供上位机对照处置（显示+禁止启动） */
static void send_feedback(void)
{
    if (sdk.ERR != 0u) {
        /* 报警态（ERR=1 过载 / 2 故障）：无论运行与否一律只发短帧
         * （联机位+ERR），不上报转速/电流——上位机收到非 0 的 ERR
         * 即显示报警态（E+报警码），运行数据在报警期间无意义 */
        uint8_t d[1u + 1u];                       /* = 2 */
        d[0] = sdk.SoftwareRun_flag;              /* 联机握手位 */
        d[1] = sdk.ERR;                           /* ERR 三态：0=正常 1=过载 2=故障 */
        frame_flush(CMD_FEEDBACK, d, (uint8_t)(1u + 1u));
    } else if (sdk.MotorRun) {
        /* 运行中：全量 7 字节 */
        uint8_t d[1u + RPM_BYTES + 2u + 1u];    /* = 7 */
        d[0] = sdk.SoftwareRun_flag;              /* 联机握手位 */
        d[1] = (uint8_t)((sdk.Rpm >> 16) & 0xFFu); /* 实时转速 高字节 */
        d[2] = (uint8_t)((sdk.Rpm >> 8) & 0xFFu); /* 实时转速 中字节 */
        d[3] = (uint8_t)(sdk.Rpm & 0xFFu);         /* 实时转速 低字节 */
        d[4] = (uint8_t)(sdk.Electricity >> 8);    /* 电流 高字节 */
        d[5] = (uint8_t)(sdk.Electricity & 0xFFu);/* 电流 低字节 */
        d[6] = sdk.ERR;                            /* ERR 三态：0=正常 1=过载 2=故障 */
        frame_flush(CMD_FEEDBACK, d, (uint8_t)(1u + RPM_BYTES + 2u + 1u));
    } else {
        /* 停止（联机保持）：短心跳 2 字节（转速/电流不上传） */
        uint8_t d[1u + 1u];                       /* = 2 */
        d[0] = sdk.SoftwareRun_flag;              /* 联机握手位（链路建立后恒 1） */
        d[1] = sdk.ERR;                           /* ERR 三态：0=正常 1=过载 2=故障 */
        frame_flush(CMD_FEEDBACK, d, (uint8_t)(1u + 1u));
    }
}

/* ============================================================
 * （简化版·无 CRC）原 rx_validate 整帧双重 CRC 验证函数已移除——
 * 帧完整即有效（帧头+LEN 合法+攒齐），无校验字节
 * ============================================================ */

/* ============================================================
 * 状态位直接编辑 —— 结构体输入模式核心：
 * 上位机指令/按键触发时直接修改对应设置位（如启动位/停止位），
 * 结构体即下位机后端控制；Mic_Sdk_Exec 仅镜像输出到硬件
 * （0x0A 模拟按键与实体按键扫描共用这三个编辑函数）
 * ============================================================ */
static void run_on(void)    /* 启动位编辑：MotorRun=1 MotorStop=0（互锁） */
{
    sdk.MotorRun  = 1;
    sdk.MotorStop = 0;
}

static void run_off(void)   /* 停止位编辑：MotorRun=0 MotorStop=1（互锁）+ 转速归零 */
{
    sdk.MotorRun  = 0;
    sdk.MotorStop = 1;
    sdk.Rpm       = 0;
}

/* ----------（运行裁决已移除：启停位由 0x02/0x03/按键事件直接编辑）---------- */

static void gear_toggle(void)   /* 高低速档位切换（转速+电流成对直接改写） */
{
    static uint8_t gear_hi = 0;         /* 0:低速档 1:高速档（上电默认低速；static 局部变量，rx_init 不复位） */

    /* 档位参数未配置（宏全 0）时不改写——防运行中切档把
     * SetRpm 归零；上位机高低速控制接口：0x0A 按键信号帧
     * data[0]=3（GDQHAJ）data[1]=1 按下沿触发本切换，
     * 上位机接入设计后填好四个 GEAR 宏即可启用 */
    if (GEAR_LO_RPM == 0u && GEAR_HI_RPM == 0u)
        return;

    gear_hi = (uint8_t)!gear_hi;
    sdk.SetRpm        = gear_hi ? GEAR_HI_RPM : GEAR_LO_RPM;
    sdk.SetElecricity = gear_hi ? GEAR_HI_MA  : GEAR_LO_MA;
    /* 不置 SetRpm_flag——它是瞬时设置状态位（0x01 专用），
     * 切档仅改参数值，启停由 MotorRun 表达 */
}

/* ============================================================
 * 拆包分辨一帧任务 —— 三步模型：
 *   ① 判断上位机放置位：帧内 cmd=任务编号、data=参数（放置位）
 *   ② 校验通过 → 直接编辑指令对应的旗帜位；校验失败 → 置负位
 *      （如 0x01 失败置 SetRpm_flag=0 未设置；无效帧不置位，
 *       旗帜位保持/回到负态即默认态——情况由位本身表达）
 *   ③ 编辑完成 → 回 ACK（结果字节 = 对应指令所写旗帜位的当前值）
 *
 * ACK 结果字节 = 对应指令所写旗帜位的直接镜像（不重算、非独立错误码）：
 *      0x01 → SetRpm_flag      （1=已设置 0=未设置）
 *      0x02 → SoftwareRun_flag （1=软件运行 0=未运行）
 *              ——不用 MotorRun：脚踏也会置位，镜像有歧义；
 *                SoftwareRun_flag 仅 0x02/0x03 写，是指令自身的位
 *      0x03 → MotorStop       （1=已停止 0=运行中（脚踏/面板按住））
 *      0x04 → MotorDir        （1=正转 2=反转——方向值本身）
 *      0x0A → 对应按键位当前电平（TBAJ/ZXAJ/GDQHAJ 由 data[0] 选）
 *   原来ACK的需求仅为"指令已收到"（停止重发+唤醒等待），结果值
 *   上位机从不分支判断——旗帜位镜像满足全部需求，且上位机可拿
 *   "回显值 vs 下发值"交叉验证执行结果，与结构体输入模式统一
 * ============================================================ */
static uint8_t rx_resolve(void)
{
    uint8_t  cmd = rx_peek(1);      /* 任务编号 */
    uint8_t  len = rx_peek(2);      /* DATA 长度 */
    uint8_t  ack;                   /* ACK 结果 = 对应旗帜位当前值 */
    uint8_t  set_done;              /* 0x01 设置流程完成标志（收尾用） */
    uint32_t val32;                 /* 参数暂存 */

    switch (cmd) {
    case 0x01:   /* 任务0x01·设置转速（启动请求）——三步顺序：
                  *   ① 判断是调整转速指令：cmd=0x01 且 LEN=4
                  *   ② SetRpm_flag 判断放置位：已置 1（正在设置状态）
                  *      则无法进行修改直接拒绝；为 0 才置 1 进入设置
                  *   ③ 解析 4 字节值（如 FF FF=65535）赋给 SetRpm，
                  *      置 1+赋值完成后由尾部反馈给上位机
                  *      （ACK 置 1 + 补发 0x81 长帧），完成前不再接受修改；
                  *      值超限撤回请求位退出设置状态 */
        if (sdk.SetRpm_flag) break;      /* 正在设置状态：无法进行修改 */
        if (len != 4u) break;            /* 非调整转速帧（LEN≠4）：不执行 */
        sdk.SetRpm_flag = 1;             /* 置 1：启动请求 + 设置状态位 */
        val32 = ((uint32_t)rx_peek(3) << 24)
              | ((uint32_t)rx_peek(4) << 16)
              | ((uint32_t)rx_peek(5) << 8)
              | rx_peek(6);
        if (val32 > RPM_MAX) {           /* 超限：撤回请求位，退出设置状态 */
            sdk.SetRpm_flag = 0;
            break;
        }
        sdk.SetRpm = val32;              /* 解析完成 → 赋值设置转速参数 */
        break;
    case 0x02:   /* 任务0x02·启动：LEN 恒好 0 → 置通讯成功位 + 直接操作
                  * 启停位（SoftwareRun_flag=1 握手成功 + MotorRun=1
                  * MotorStop=0 互锁启动电机）；无效帧不执行 */
        if (len == 0) {
            sdk.SoftwareRun_flag = 1;
            run_on();
        }
        break;
    case 0x03:   /* 任务0x03·停止：LEN 恒好 0 → 直接操作启停位
                  * （MotorRun=0 MotorStop=1 停止电机）；脚踏/面板按住时
                  * 不停止（脚踏优先）。
                  * 联机位随连接状态：DATA=0（无参·旧语义）保持不变；
                  * DATA=1（LEN=1）=断开——上位机关闭程序前发送，
                  * SoftwareRun_flag 清 0（心跳首位回 0=已断开），
                  * 下次程序启动 0x02 重新置 1（重新握手） */
        if (len == 0) {
            if (!sdk.TBAJ && !sdk.ZXAJ)
                run_off();
        } else if (len == 1 && rx_peek(3) == 1u) {
            sdk.SoftwareRun_flag = 0;   /* 上位机断开：联机位清零 */
            if (!sdk.TBAJ && !sdk.ZXAJ)
                run_off();              /* 顺带停止（断开不残留运行） */
        }
        break;
    case 0x04:   /* 任务0x04·设置方向：LEN 恰好 1，值必须是 1 或 2；
                  * 运行中（MotorRun=1）拒改——防全速瞬间反向的电流
                  * 冲击/机械损伤，静止（MotorRun=0）可改；
                  * 拒改时 ACK 回显旧 MotorDir，上位机对照下发值即知未执行 */
        if (len != 1) break;
        if (sdk.MotorRun) break;        /* 运行中：不执行（判断启动位） */
        if (rx_peek(3) == 1u || rx_peek(3) == 2u)
            sdk.MotorDir = rx_peek(3); /* 置位：方向信号 */
        break;
    case 0x0A:   /* 任务0x0A·按键信号：LEN 恰好 2（按键类型 + 按下状态）
                  * data[0] 按键类型：1=TBAJ 脚踏 2=ZXAJ 面板 3=GDQHAJ 高低速
                  * data[1] 按下状态：0=松开 1=按下（与实体按键同语义）
                  * 处理流程：信号进来 → 先对对应功能进行处理（key_event_xxx
                  * 内部①功能处理：启停/档位）→ 再对参数进行处理（②SetRpm/
                  * SetElecricity 跟随），与实体按键同一入口同一路径
                  * （注：不用 0x05——完整版协议中 0x05=设置电流，避免撞码） */
        if (len == 2 && rx_peek(4) <= 1u) {
            switch (rx_peek(3)) {
            case 1: key_event_tba(rx_peek(4)); break;  /* 脚踏：功能→参数 */
            case 2: key_event_zx(rx_peek(4));  break;  /* 面板：功能→参数 */
            case 3: key_event_gdq(rx_peek(4)); break;  /* 高低速：功能→参数 */
            default: break;                              /* 未知按键类型：不执行 */
            }
        }
        break;
    case CMD_QUERY_SETRPM:  /* 0x0B 查询 SetRpm：纯查询不修改任何状态，
                             * ACK 结果=1（查询成功），send_ack 附带 SetRpm 数值 */
        ack = 1u;
        break;
    default:     /* 未知任务编号：不执行（缩小版未实现的指令均落此分支） */
        break;
    }

    /* 0x01 设置流程收尾：清 SetRpm_flag——给上位机发送确认信息时
     * 设置状态位已置 0（设置流程结束，允许下一次修改）。
     * 注：不写 sdk.Rpm——实测转速归采集层，设定值不冒充实测值 */
    set_done = (uint8_t)(cmd == 0x01u && sdk.SetRpm_flag);
    if (set_done) {
        sdk.SetRpm_flag = 0;      /* 设置状态结束（发送前置 0） */
    }

    /* ACK 结果 = 对应指令所写旗帜位的当前值（直接镜像，不重算） */
    switch (cmd) {
    case 0x01: ack = sdk.SetRpm_flag;      break;  /* 0=设置已完成回 0（长帧反馈为准） */
    case 0x02: ack = sdk.SoftwareRun_flag; break;  /* 1=联机握手成功 0=未握手 */
    case 0x03: ack = sdk.MotorStop;        break;  /* 1=已停止 0=运行中（脚踏/面板按住） */
    case 0x04: ack = sdk.MotorDir;         break;  /* 1=正转 2=反转（方向值本身） */
    case 0x0A:   /* 按键位当前电平（data[0] 选对应旗帜位） */
        switch (rx_peek(3)) {
        case 1:  ack = sdk.TBAJ;   break;    /* 脚踏位电平 */
        case 2:  ack = sdk.ZXAJ;   break;    /* 面板位电平 */
        case 3:  ack = sdk.GDQHAJ; break;    /* 高低位电平 */
        default: ack = 0;          break;    /* 未知按键类型：无对应旗帜位 */
        }
        break;
    default:   ack = 0;                    break;  /* 未知指令：无对应旗帜位可镜像 */
    }

    send_ack(cmd, ack);             /* 回 ACK：结果字节 = 旗帜位当前值镜像 */

    /* 0x01 设置完成 / 0x02 启动 → ACK 之后立即补发一帧 0x81（周期心跳
     * 之外中间多发一次）：携带 Rpm——0x01 时 Rpm=新设定值（确认修改
     * 生效），0x02 时 run_on 已完成、长帧反馈当前转速——上位机收到
     * 此帧即确认，不再进行设置/启动操作 */
    if (set_done || (cmd == 0x02u && ack == 1u)) {
        send_feedback();
    }

    return 0;                        /* 已编辑的旗帜位保持，供 Exec 镜像输出与心跳上报 */
}

/* ============================================================
 * 主循环消费 —— 有数据即解析（不等攒满）：
 *   · 帧攒齐（available ≥ total）→ 立即解析，下一圈即应答 ACK；
 *   · 帧未攒齐 → 两种情况：
 *       在途（空闲计数未耗尽，字节仍在到达）→ 保留等待，绝不切帧；
 *       卡死（连续 RX_IDLE_POLLS 圈无新字节仍不齐）→ 假帧头/残留，
 *       丢队首 1 字节清障重找——假头后面若跟着真帧，最终能解出
 *   单生产者单消费者免锁：本函数只动 rx_r，中断只动 rx_w
 * ============================================================ */
uint8_t rx_poll(void)
{
    uint8_t len;          /* 本帧 LEN */
    uint8_t total;        /* 本帧总长 = LEN + 3（简化版无 CRC） */

    while (rx_available() > 0) {
        /* 1. 找帧头：队首不是 0x2F 就是噪声，丢 1 字节再查 */
        if (rx_peek(0) != FRAME_HEAD) {
            rx_drop(1);
            continue;
        }

        /* 2. 帧头 3 字节不齐：在途 → 倒计数等待；卡死 → 丢 1 字节 */
        if (rx_available() < 3) {
            if (rx_idle_cnt > 0) { rx_idle_cnt--; break; }
            rx_drop(1);
            continue;
        }

        /* 3. 读 LEN 算帧总长：LEN 非法则该头是假头，丢 1 字节重找 */
        len = rx_peek(2);
        if (len > RX_DATA_MAX) {
            rx_drop(1);
            continue;
        }
        total = (uint8_t)(len + 3);

        /* 4. 帧未攒齐：在途 → 倒计数等待（不切帧）；卡死 → 丢 1 字节清障 */
        if (rx_available() < total) {
            if (rx_idle_cnt > 0) { rx_idle_cnt--; break; }
            rx_drop(1);
            continue;
        }

        /* 5. 帧完整即执行（简化版无 CRC 验证）：
         * rx_resolve 内直接编辑旗帜位并回 ACK（结果=旗帜位
         * 当前值镜像），返回值恒 0（无效/未知指令不执行、仅回显） */
        rx_resolve();
        rx_drop(total);             /* 整帧消费 */
    }
    return 0;                          /* 返回值恒 0：旗帜位已直接编辑，无累计计数 */
}

/* ============================================================
 * 心跳上报 —— 主循环周期调用，每次发一帧 0x81 综合反馈
 * 帧长随运行状态变化：未启动短心跳 2 字节 / 运行中全量 7 字节
 * 注意：心跳必须在联机前就持续发送（上位机探测靠它发现设备），
 *       SoftwareRun_flag 不能作为心跳门控（会死等指令堵死主循环）——
 *       未启动时也要发短心跳保持设备可见
 * ============================================================ */
void heart_feedback(void)
{
    send_feedback();   /* 组帧 + 发送一体 */
}

/* ============================================================
 * 硬件输出 —— 结构体状态镜像到硬件（纯输出，不做任何裁决）：
 * 状态位由指令/按键触发处直接编辑（run_on/run_off/gear_toggle），
 * 本函数只把当前结构体状态输出到对应硬件：
 *   MotorRun / MotorStop  → 电机使能/刹车（互锁位，直接对应电平）
 *   MotorPower           → 供电开关（应用层本地写，无协议指令）
 *   MotorDir              → 方向输出（1=正转 2=反转）
 *   MotorRun + SetRpm_flag → 运行且已设转速才输出 PWM（否则归零）
 *   SetElecricity         → 电流限制 DAC 输出
 * TODO：按实际 MCU 替换各 TODO 行为寄存器/IO 操作
 * ============================================================ */
static void Mic_Sdk_Exec(void)
{


    /* 方向：直接镜像（1=正转 2=反转） */
    /* TODO: DIR_IO = (sdk.MotorDir == 2u); */

    /* 转速给定：运行（MotorRun）才输出，停止/未运行给定归零
     * ——与使能脚同起同落，不依赖硬件使能兜底。
     * 注：不能用 SetRpm_flag 做门控——它是瞬时设置状态位
     * （rx_resolve 发送确认前已清 0），Exec 运行时恒为 0 */
    if (sdk.MotorRun) {
        /* TODO: SetPwm(sdk.SetRpm);  转速给定输出（≤99999） */
    } else {
        /* TODO: SetPwm(0);           给定归零（停止态不残留转速） */
    }

    /* 注：sdk.Rpm 不在此赋值——实测转速由下位机采集层（编码器/
     * 霍尔测速）直接写入 sdk，心跳 0x81 长帧 d[1..3] 原样上报；
     * 无测速硬件时 Rpm 保持 0（真实反映"未测量"），上位机显示
     * 由本地动画承担，不用设定值冒充实测值（回声无信息量） */

    /* 电流限制 */
    /* TODO: SetDac(sdk.SetElecricity);  电流限制 DAC 输出（mA） */
}

/* ============================================================
 * 应用层主循环 —— while(1) 里每圈调用一次
 *   ① 指令解析：rx_poll（指令到达直接编辑 sdk 设置位）
 *   ② 硬件输出：Mic_Sdk_Exec 镜像 sdk 状态 → 驱动硬件
 *   ③ 心跳：分频上报 0x81（每 HEART_DIV 圈一帧 ≈10ms；Rpm/
 *      Electricity/ERR 实时数据由下位机采集层直接写入 sdk 提供，
 *      此处不经过本模块；实体按键事件由原机工程轮子直接调
 *      key_event_tba/zx/gdq 接入，本模块不做按键扫描消抖；
 *      uart_send_byte 为阻塞发送，如需精确上报周期，改用硬件
 *      定时器按固定间隔单独调 heart_feedback()）
 * ============================================================ */
void Mic_Sdk_Poll(void)
{
    static uint8_t heart_cnt = 0;   /* 心跳分频计数（static 局部变量，rx_init 不复位） */

    rx_poll();            /* ① 指令解析：直接编辑对应设置位（启停位在指令/按键事件内直接编辑） */
    Mic_Sdk_Exec();       /* ② 硬件输出：结构体状态镜像到 IO/PWM/DAC */

    /* ③ 周期心跳：每 HEART_DIV 圈发一帧（≈10ms，分频降低总线占用
     * 与上位机日志量；rx_resolve 的 ACK/补发帧不受分频影响即时发） */
    if (++heart_cnt >= HEART_DIV) {
        heart_cnt = 0;
        heart_feedback();
    }
}
