# Printcsv.c 单片机SD卡存储版本

## 功能说明
该版本的Printcsv.c专为单片机SD卡存储设计，使用FatFs文件系统库将数据写入CSV文件。

## 主要特性
1. 基于FatFs文件系统，适配单片机SD卡存储
2. 支持自定义文件名或默认文件名
3. 自动检查文件是否为空并决定是否写入表头
4. 支持写入多行多列数据
5. 使用TCHAR兼容Unicode

## 文件修改
- `Printcsv.c`: 核心实现文件，已修改为FatFs版本
- `PrintCsv.h`: 头文件，已修改为FatFs版本

## 使用方法

### 1. 配置FatFs
需要在项目中配置FatFs文件系统：
- 确保FatFs库已正确添加到项目
- 根据硬件平台配置`ffconf.h`（如SDIO/SPIFlash接口等）

### 2. 初始化FatFs
在使用`PrintCsv`函数前，需要初始化并挂载FatFs：
```c
FATFS fs;
FRESULT res = f_mount(&fs, _T("0:"), 1);  // 挂载SD卡设备号"0:"
if (res != FR_OK) {
    // 处理挂载失败
}
```

### 3. 准备数据
数据以一维数组的形式存储，按行顺序排列：
```c
const TCHAR* header[] = {_T("Time"), _T("Temperature"), _T("Pressure"), _T("HeartRate")};
const TCHAR* data[] = {
    _T("08:00:00"), _T("36.5"), _T("98"), _T("72"),  // 第一行数据
    _T("08:05:00"), _T("36.6"), _T("97"), _T("75"),  // 第二行数据
    _T("08:10:00"), _T("36.4"), _T("99"), _T("70")   // 第三行数据
};
```

### 4. 调用PrintCsv函数
```c
int rows = 3;   // 数据行数
int cols = 4;   // 数据列数

// 使用默认文件名写入数据（Data.csv）
int result = PrintCsv(NULL, header, data, rows, cols);
if (result == 0) {
    // 数据写入成功
} else {
    // 数据写入失败，错误码为FatFs错误码
}
```

### 5. 卸载FatFs
使用完毕后，卸载FatFs：
```c
f_mount(NULL, _T("0:"), 0);
```

## 参数说明
### PrintCsv函数
```c
int PrintCsv(const TCHAR* filename, const TCHAR** header, const TCHAR** data, int rows, int cols);
```
- `filename`: 自定义文件名（如NULL则使用默认名"Data.csv"）
- `header`: 表头数组（如NULL则不写入表头）
- `data`: 数据数组（一维数组，按行存储）
- `rows`: 数据行数
- `cols`: 数据列数
- 返回值: 0-成功，其他-FatFs错误码

## 错误码
返回值为FatFs的错误码，常见错误码包括：
- `FR_OK`: 成功
- `FR_DISK_ERR`: 磁盘错误
- `FR_INT_ERR`: 内部错误
- `FR_NOT_READY`: 设备未就绪（如SD卡未插入）
- `FR_NO_FILE`: 文件不存在
- `FR_DENIED`: 访问被拒绝

## 注意事项
1. 确保SD卡已正确插入并初始化
2. 使用`f_sync`函数确保数据写入SD卡（已在函数内部实现）
3. 文件名建议使用8.3格式（兼容旧版FAT文件系统）
4. 需要为SD卡预留足够的存储空间
5. 高频率写入时注意SD卡的寿命

## 示例代码
可查看Printcsv.c中的`PRINTCSV_EXAMPLE`宏定义部分，包含完整的使用示例。

## 编译说明
- 需要包含FatFs头文件路径
- 需要链接FatFs库文件
- 建议使用支持TCHAR的编译器

## 版本说明
V1.0: 初始版本，基于FatFs实现SD卡CSV存储