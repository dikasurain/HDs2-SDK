#include <stdio.h>
#define BUFFER_SIZE 1024
#include "serial.h"
#include <windows.h>
#include <time.h>
#include <string.h>
#include <errno.h>

// CRC16校验函数声明
unsigned short crc16(unsigned char* data, int len);

// 全局变量声明
PORT g_port = NULL;
FILE* g_csv_file = NULL;
char g_csv_filename[64] = "";
volatile int g_running = 0;

// CRC16校验函数实现
unsigned short crc16(unsigned char* data, int len) {
    unsigned int crc = 0xFFFF;
    int i, j;
    for (i = 0; i < len; i++) {
        crc ^= data[i];
        for (j = 0; j < 8; j++) {
            if (crc & 0x0001) {
                crc = (crc >> 1) ^ 0xA001;
            } else {
                crc >>= 1;
            }
        }
    }
    return (unsigned short)crc;
}

// 启动数据采集
int start_data_collection(int port_num, int baud_rate, int data_bits, int stop_bits, int parity) {
    if (g_running) {
        printf("数据采集已在运行\n");
        return -1;
    }

    // 生成CSV文件名
    time_t now = time(NULL);
    struct tm* t = localtime(&now);
    if (t == NULL) {
        printf("获取当前时间失败\n");
        perror("localtime error");
        return -1;
    }
    sprintf(g_csv_filename, "Data%04d-%02d-%02d %02d-%02d-%02d.csv", 
             t->tm_year + 1900, t->tm_mon + 1, t->tm_mday, 
             t->tm_hour, t->tm_min, t->tm_sec);
    printf("生成的CSV文件名: %s\n", g_csv_filename);

    // 打开CSV文件
    g_csv_file = fopen(g_csv_filename, "w");
    if (!g_csv_file) {
        printf("无法创建CSV文件: %s\n", g_csv_filename);
        perror("fopen error");
        return -1;
    }
    printf("成功创建CSV文件: %s\n", g_csv_filename);

    // 写入表头
    int ret = fprintf(g_csv_file, "Date,Time,Preading,Treading\n");
    if (ret < 0) {
        perror("fprintf header error");
    } else {
        printf("成功写入表头，共%d个字符\n", ret);
    }
    ret = fflush(g_csv_file);
    if (ret < 0) {
        perror("fflush header error");
    } else {
        printf("成功刷新表头到磁盘\n");
    }

    // 初始化端口
    printf("尝试打开COM%d\n", port_num);
    g_port = serial_init(port_num, baud_rate, data_bits, stop_bits, parity);
    if (g_port == NULL) {
        printf("端口初始化失败\n");
        fclose(g_csv_file);
        g_csv_file = NULL;
        return -1;
    }
    printf("成功打开COM%d\n", port_num);

    g_running = 1;
    printf("数据采集已启动，CSV文件: %s\n", g_csv_filename);

    // 数据处理相关变量
    char buff[1024] = {0};
    int rcv_len = 0;
    static char static_buff[1024] = {0};
    static int static_index = 0;
    int sample_count = 0;
    int first_packet = 1;

    while (g_running && sample_count < 100) {
        // 发送命令
        unsigned char cmd[] = {0xCE, 0x05, 0x10, 0x12, 0xA3};
        unsigned char send_buf[7];
        memcpy(send_buf, cmd, sizeof(cmd));
        unsigned int crc = 0xFFFF;
        int i, j;
        for (i = 0; i < sizeof(cmd); i++) {
            crc ^= cmd[i];
            for (j = 0; j < 8; j++) {
                if (crc & 0x0001) {
                    crc = (crc >> 1) ^ 0xA001;
                } else {
                    crc >>= 1;
                }
            }
        }
        send_buf[5] = (unsigned char)crc;
        send_buf[6] = (unsigned char)(crc >> 8);
        Serial_SendData(g_port, (const char*)send_buf, 7);

        // 接收数据
        rcv_len = Serial_ReciveData(g_port, (unsigned char*)buff, 1024);
        if (rcv_len > 0) {
            // 打印收到的数据长度和内容
            printf("Received %d bytes: ", rcv_len);
            int k;
            for (k = 0; k < rcv_len; k++) {
                printf("%02X ", (unsigned char)buff[k]);
            }
            printf("\n");
            // 将新接收的数据追加到静态缓冲区
            if (static_index + rcv_len < sizeof(static_buff)) {
                memcpy(static_buff + static_index, buff, rcv_len);
                static_index += rcv_len;
            } else {
                static_index = 0; // 缓冲区溢出，重置
            }
        } else if (rcv_len < 0) {
            printf("Serial_ReciveData returned error: %d\n", rcv_len);
        }

        // 处理静态缓冲区，首先检测停止命令
        int current_pos = 0;
        while (static_index - current_pos >= 5) {
            unsigned char cmd[] = {0xCE, 0x05, 0x11, 0xD3, 0x63};
            if (memcmp(static_buff + current_pos, cmd, 5) == 0) {
                printf("检测到停止命令CE 05 11 D3 63，正在停止数据采集...\n");
                g_running = 0;
                // 将剩余数据移动到缓冲区开头
                if (static_index - (current_pos + 5) > 0) {
                    memmove(static_buff, static_buff + current_pos + 5, static_index - (current_pos + 5));
                    static_index = static_index - (current_pos + 5);
                } else {
                    static_index = 0;
                }
                break;
            }
            current_pos++;
        }

        // 处理静态缓冲区，查找16字节数据包
            current_pos = 0;
            while (static_index - current_pos >= 16) {
                if ((unsigned char)static_buff[current_pos] == 0xCE) {
                    // 打印原始16字节数据
                    printf("Received 16-byte packet: ");
                    int i;
                    for (i = 0; i < 16; i++) {
                        printf("%02X ", (unsigned char)static_buff[current_pos + i]);
                    }
                    printf("\n");

                    // 计算CRC校验（包含长度和数据部分，共13字节：current_pos+1到+13）
                    unsigned short crc = crc16((unsigned char*)&static_buff[current_pos + 1], 13);
                    // 从数据包中提取CRC（第14-15字节，假设为小端格式）
                    unsigned short packet_crc = static_buff[current_pos + 14] | ((unsigned short)static_buff[current_pos + 15] << 8);

                    printf("Calculated CRC: %04X, Packet CRC: %04X\n", crc, packet_crc);

                    // 注释掉CRC检查，处理所有数据包
                    // if (crc == packet_crc) {
                        // 解析压力和温度数据
                        float preading = 0.0f;
                        float treading = 0.0f;
                        memcpy(&preading, &static_buff[current_pos + 2], 4);
                        memcpy(&treading, &static_buff[current_pos + 6], 4);

                        printf("Preading: %.2f, Treading: %.2f\n", preading, treading);

                        // 获取当前时间
                        time_t now = time(NULL);
                        if (now == -1) {
                            perror("time error");
                            break;
                        }
                        struct tm* t = localtime(&now);
                        if (t == NULL) {
                            perror("localtime error");
                            break;
                        }

                        // 将数据写入CSV文件
                        int ret = fprintf(g_csv_file, "%04d-%02d-%02d,%02d:%02d:%02d,%.2f,%.2f\n", 
                                t->tm_year + 1900, t->tm_mon + 1, t->tm_mday,
                                t->tm_hour, t->tm_min, t->tm_sec,
                                preading, treading);
                        if (ret < 0) {
                            perror("fprintf data error");
                        }
                        ret = fflush(g_csv_file); // 确保数据立即写入CSV文件
                        if (ret < 0) {
                            perror("fflush data error");
                        }

                        // 增加采集次数
                        sample_count++;
                        if (first_packet) {
                            printf("已处理第一个数据包\n");
                            first_packet = 0;
                        }

                        // 移动到下一个数据包
                        current_pos += 16;
                    // } else {
                        // // CRC校验失败，跳过该字节
                        // printf("CRC校验失败\n");
                        // current_pos++;
                    // }
                } else {
                    // 不是有效起始字节，向前移动
                    current_pos++;
                }
            }

            // 将剩余数据移动到缓冲区开头
            if (current_pos > 0) {
                memmove(static_buff, static_buff + current_pos, static_index - current_pos);
                static_index -= current_pos;
            }
        Sleep(100);
    }

    return 0;
}

// 停止数据采集
int stop_data_collection() {
    if (!g_running) {
        printf("数据采集未在运行\n");
        return -1;
    }

    g_running = 0;
    Sleep(150); // 等待数据处理完成

    // 关闭CSV文件
    if (g_csv_file != NULL) {
        fclose(g_csv_file);
        g_csv_file = NULL;
        printf("数据已保存到CSV文件: %s\n", g_csv_filename);
    }

    // 关闭端口
    if (g_port != NULL) {
        ClosePort(g_port);
        g_port = NULL;
        printf("端口已关闭\n");
    }

    return 0;
}





// 主函数
int main() {
    printf("开始数据采集程序\n");

    // 启动数据采集（COM7，115200波特率，8数据位，1停止位，无校验）
    if (start_data_collection(7, 115200, 8, 1, 0) != 0) {
        printf("数据采集启动失败\n");
        return 1;
    }

    printf("输入1停止数据采集...\n");
    char c;
    while ((c = getchar()) != '1') {
        // 等待用户输入1
    }

    // 停止数据采集
    stop_data_collection();

    printf("程序已退出\n");
    return 0;
}
