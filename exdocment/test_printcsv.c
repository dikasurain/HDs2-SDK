#include <stdio.h>
#include <string.h>
#include "PrintCsv.h"

int main() {
    // Example 1: Basic usage with header and data
    printf("=== Example 1: Basic CSV writing ===\n");
    const char* header[] = {"姓名", "年龄", "性别", "职业"};
    const char* data1[] = {
        "张三", "25", "男", "程序员",
        "李四", "30", "女", "教师",
        "王五", "35", "男", "医生"
    };
    int result1 = PrintCsv(NULL, header, data1, 3, 4);
    printf("Example 1 %s\n\n", result1 == 0 ? "成功" : "失败");

    // Example 2: Writing without header to existing file
    printf("=== Example 2: Appending to existing CSV ===\n");
    const char* data2[] = {
        "赵六", "28", "男", "工程师",
        "孙七", "26", "女", "设计师"
    };
    // Note: Use NULL filename to generate new file, use same filename to append
    // Here we'll append to a specific file
    int result2 = PrintCsv("test_data.csv", NULL, data2, 2, 4);
    printf("Example 2 %s\n\n", result2 == 0 ? "成功" : "失败");

    // Example 3: Minimal usage (no header, single row)
    printf("=== Example 3: Single row without header ===\n");
    const char* data3[] = {"2024-01-01", "10:00", "300", "OK"};
    int result3 = PrintCsv(NULL, NULL, data3, 1, 4);
    printf("Example 3 %s\n\n", result3 == 0 ? "成功" : "失败");

    printf("所有测试完成\n");
    return 0;
}