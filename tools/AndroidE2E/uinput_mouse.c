/* 가상 마우스(uinput) — 에뮬레이터에서 진짜 마우스 단추 클릭을 만든다(adb root 필요).
   쓰기: uinput_mouse <dx> <dy> right|left — 포인터를 옮긴 뒤 단추를 한 번 누른다. */
#include <fcntl.h>
#include <linux/uinput.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

static void emit(int fd, int type, int code, int value) {
    struct input_event ev;
    memset(&ev, 0, sizeof ev);
    ev.type = type;
    ev.code = code;
    ev.value = value;
    if (write(fd, &ev, sizeof ev) != sizeof ev) perror("write");
}

int main(int argc, char **argv) {
    int dx = argc > 1 ? atoi(argv[1]) : 0, dy = argc > 2 ? atoi(argv[2]) : 0;
    int button = argc > 3 && strcmp(argv[3], "left") == 0 ? BTN_LEFT : BTN_RIGHT;
    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) { perror("open /dev/uinput"); return 1; }
    ioctl(fd, UI_SET_EVBIT, EV_KEY);
    ioctl(fd, UI_SET_KEYBIT, BTN_LEFT);
    ioctl(fd, UI_SET_KEYBIT, BTN_RIGHT);
    ioctl(fd, UI_SET_KEYBIT, BTN_MIDDLE);
    ioctl(fd, UI_SET_EVBIT, EV_REL);
    ioctl(fd, UI_SET_RELBIT, REL_X);
    ioctl(fd, UI_SET_RELBIT, REL_Y);
    ioctl(fd, UI_SET_RELBIT, REL_WHEEL);
    struct uinput_setup setup;
    memset(&setup, 0, sizeof setup);
    setup.id.bustype = BUS_USB;
    setup.id.vendor = 0x1234;
    setup.id.product = 0x5678;
    strcpy(setup.name, "e2e-virtual-mouse");
    if (ioctl(fd, UI_DEV_SETUP, &setup) < 0 || ioctl(fd, UI_DEV_CREATE) < 0) { perror("uinput setup"); return 1; }
    sleep(2); /* InputReader 가 새 장치를 알아챌 때까지 */
    for (int i = 0; i < 10; i++) {
        emit(fd, EV_REL, REL_X, dx / 10);
        emit(fd, EV_REL, REL_Y, dy / 10);
        emit(fd, EV_SYN, SYN_REPORT, 0);
        usleep(20000);
    }
    usleep(300000);
    emit(fd, EV_KEY, button, 1);
    emit(fd, EV_SYN, SYN_REPORT, 0);
    usleep(80000);
    emit(fd, EV_KEY, button, 0);
    emit(fd, EV_SYN, SYN_REPORT, 0);
    sleep(1);
    ioctl(fd, UI_DEV_DESTROY);
    close(fd);
    printf("clicked %s\n", button == BTN_RIGHT ? "right" : "left");
    return 0;
}
