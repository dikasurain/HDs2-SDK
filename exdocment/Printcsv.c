#include "ff.h"   // FatFs文件系统头文件
#include "string.h" // 字符串处理

// PrintCsv函数：将数据写入SD卡中的CSV文件
// 参数：
//   filename - 自定义文件名（NULL则使用默认名）
//   header - 表头数组（NULL则不写表头）
//   data - 数据数组（一维数组，按行存储）
//   rows - 数据行数
//   cols - 数据列数
// 返回值：0-成功，其他-FatFs错误码
int PrintCsv(const TCHAR* filename, const TCHAR** header, const TCHAR** data, int rows, int cols) {
    FIL csv_file;               // FatFs文件对象
    TCHAR generated_filename[64] = {0};  // 生成的文件名
    const TCHAR* use_filename = filename;  // 实际使用的文件名
    DWORD file_size;            // 文件大小
    FRESULT res;                // FatFs操作结果
    int i, j;

    // -------------------------- 1. 确定文件名 --------------------------
    if (filename == NULL || _tcslen(filename) == 0) {
        // 默认文件名（无时间函数时使用简单命名）
        _tcscpy(generated_filename, _T("Data.csv"));
        use_filename = generated_filename;
    }

    // -------------------------- 2. 打开/创建CSV文件 --------------------------
    // 模式：FA_OPEN_ALWAYS(不存在则创建) | FA_READ(读权限) | FA_WRITE(写权限)
    res = f_open(&csv_file, use_filename, FA_OPEN_ALWAYS | FA_READ | FA_WRITE);
    if (res != FR_OK) {
        return res;  // 返回FatFs错误码
    }

    // -------------------------- 3. 检查是否需要写入表头 --------------------------
    res = f_size(&csv_file, &file_size);  // 获取文件大小
    if (res != FR_OK) {
        f_close(&csv_file);
        return res;
    }

    if (file_size == 0 && header != NULL) {
        // 文件为空且提供了表头，写入表头
        for (j = 0; j < cols; j++) {
            if (j > 0) {
                f_puts(_T(","), &csv_file);  // 列分隔符
            }
            if (header[j] != NULL) {
                f_puts(header[j], &csv_file);  // 写入表头字段
            }
        }
        f_puts(_T("\n"), &csv_file);  // 换行
    }

    // -------------------------- 4. 定位到文件末尾准备追加数据 --------------------------
    res = f_lseek(&csv_file, file_size);
    if (res != FR_OK) {
        f_close(&csv_file);
        return res;
    }

    // -------------------------- 5. 写入数据行 --------------------------
    for (i = 0; i < rows; i++) {
        for (j = 0; j < cols; j++) {
            if (j > 0) {
                f_puts(_T(","), &csv_file);  // 列分隔符
            }
            if (data[i * cols + j] != NULL) {
                f_puts(data[i * cols + j], &csv_file);  // 写入数据
            }
        }
        f_puts(_T("\n"), &csv_file);  // 行结束
    }

    // -------------------------- 6. 刷新数据到SD卡并关闭文件 --------------------------
    res = f_sync(&csv_file);  // 确保数据写入SD卡（避免数据丢失）
    f_close(&csv_file);  // 关闭文件

    return (res == FR_OK) ? 0 : res;
}

// 示例代码（可根据需要保留或删除）
#ifdef PRINTCSV_EXAMPLE
int main() {
    // 初始化FatFs（需根据硬件配置）
    FATFS fs;
    FRESULT res = f_mount(&fs, _T("0:"), 1);  // 挂载SD卡（"0:"为SD卡设备号）
    if (res != FR_OK) {
        return res;
    }

    // 示例表头
    const TCHAR* header[] = {_T("Time"), _T("Temperature"), _T("Pressure")};
    int cols = 3;
    int rows = 3;

    // 示例数据（3行×3列，一维数组存储）
    const TCHAR* data[] = {
        _T("12:00:00"), _T("37.5"), _T("100"),
        _T("12:01:00"), _T("37.6"), _T("101"),
        _T("12:02:00"), _T("37.7"), _T("102")
    };

    // 写入CSV文件
    int result = PrintCsv(NULL, header, data, rows, cols);
    if (result == 0) {
        // 输出成功信息（可替换为串口打印）
        // printf("数据写入成功\n");
    } else {
        // 输出错误信息
        // printf("数据写入失败，错误码：%d\n", result);
    }

    f_mount(NULL, _T("0:"), 0);  // 卸载SD卡
    return 0;
}
#endif