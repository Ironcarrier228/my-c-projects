#include <windows.h>
int main(void){HANDLE h=GetStdHandle(STD_OUTPUT_HANDLE);DWORD w;char s[]="REF-OK";WriteFile(h,s,6,&w,0);return 0;}
