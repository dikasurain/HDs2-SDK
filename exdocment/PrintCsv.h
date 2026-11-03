#ifndef _PRINTCSV_H_   // 防止重复包含
#define _PRINTCSV_H_

#ifdef __cplusplus
extern "C" {
#endif

#include "ff.h"   // FatFs文件系统头文件，提供TCHAR定义

// PrintCsv函数声明：将数据写入SD卡中的CSV文件
// 参数：
//   filename - 自定义文件名（NULL则使用默认名）
//   header - 表头数组（NULL则不写表头）
//   data - 数据数组（一维数组，按行存储）
//   rows - 数据行数
//   cols - 数据列数
// 返回值：0-成功，其他-FatFs错误码
int PrintCsv(const TCHAR* filename, const TCHAR** header, const TCHAR** data, int rows, int cols);

#ifdef __cplusplus
}
#endif

#endif  // _PRINTCSV_H_