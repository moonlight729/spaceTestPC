#define _POSIX_C_SOURCE 200809L
#include "usb_pretest.h"
#include <arpa/inet.h>
#include <dirent.h>
#include <errno.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/select.h>
#include <time.h>
#include <unistd.h>

#define USB_STEP_COUNT 8
struct usb_state {
    pthread_mutex_t lock;
    pthread_t thread;
    int running;
    int port;
    int step;
    int detected;
    int speed_mbps;
    int started;
    int completed;
    int failed;
    char mode[24];
    char message[160];
    char phase[24];
    char result_file[192];
    struct timespec step_started;
};
static struct usb_state state = { .lock = PTHREAD_MUTEX_INITIALIZER, .port = 18080, .mode = "pcba" };

static const char *speed_name(int step) { return step < 4 ? "usb2" : "usb3"; }
static const char *port_name(int step) { return (step % 4) < 2 ? "port1" : "port2"; }
static const char *direction_name(int step) { return step % 2 == 0 ? "normal" : "reverse"; }

static int read_speed(const char *path, int *value)
{
    FILE *file = fopen(path, "r");
    int rc;
    if (file == NULL) return -1;
    rc = fscanf(file, "%d", value);
    fclose(file);
    return rc == 1 ? 0 : -1;
}

static int detect_usb(int *speed)
{
    DIR *dir = opendir("/sys/block");
    struct dirent *entry;
    int found = 0, best = 0;
    if (dir == NULL) return 0;
    while ((entry = readdir(dir)) != NULL) {
        char device_path[512];
        char resolved[512];
        char *cursor;
        int value = 0;
        if (entry->d_name[0] != 's' || entry->d_name[1] != 'd') continue;
        snprintf(device_path, sizeof(device_path), "/sys/block/%s/device", entry->d_name);
        if (realpath(device_path, resolved) == NULL) continue;
        cursor = resolved + strlen(resolved);
        while (cursor > resolved) {
            char speed_path[600];
            snprintf(speed_path, sizeof(speed_path), "%.*s/speed", (int)(cursor - resolved), resolved);
            if (read_speed(speed_path, &value) == 0 && value > 0) break;
            while (cursor > resolved && *cursor != '/') cursor--;
            while (cursor > resolved && *cursor == '/') cursor--;
        }
        if (value > 0) {
            found = 1;
            if (value > best) best = value;
        }
    }
    closedir(dir);
    if (speed != NULL) *speed = best;
    return found;
}

static void set_result_path(void)
{
    mkdir("/userdata/factory_test/usb", 0755);
    snprintf(state.result_file, sizeof(state.result_file), "/userdata/factory_test/usb/%s_usb_test.json",
             strcmp(state.mode, "finished_product") == 0 ? "finished_product" : "pcba");
}

static void create_desktop_shortcut(void)
{
    const char *path = "/home/originflow/Desktop/USB-Test.desktop";
    FILE *file;
    mkdir("/home/originflow/Desktop", 0755);
    file = fopen(path, "w");
    if (file == NULL) return;
    fprintf(file,
            "[Desktop Entry]\n"
            "Type=Application\n"
            "Name=USB测试\n"
            "Comment=打开USB预检页面\n"
            "Exec=xdg-open http://127.0.0.1:%d/usb-test\n"
            "Icon=applications-internet\n"
            "Terminal=false\n"
            "Categories=Utility;\n",
            state.port);
    fclose(file);
    chmod(path, 0755);
    if (chown(path, 1002, 1002) != 0) {
        fprintf(stderr, "unable to set USB shortcut ownership: %s\n", strerror(errno));
    }
}

static void write_result(void)
{
    char temp[256];
    FILE *file;
    if (state.result_file[0] == '\0') return;
    snprintf(temp, sizeof(temp), "%s.tmp", state.result_file);
    file = fopen(temp, "w");
    if (file == NULL) return;
    fprintf(file, "{\"schemaVersion\":1,\"testMode\":\"%s\",\"overallResult\":\"%s\",\"step\":%d,\"usb2Cycles\":%d,\"usb3Cycles\":%d,\"speedMbps\":%d}\n",
            state.mode, state.completed && !state.failed ? "passed" : "failed", state.step,
            state.step > 4 ? 4 : state.step, state.step > 4 ? state.step - 4 : 0, state.speed_mbps);
    fflush(file);
    fsync(fileno(file));
    fclose(file);
    rename(temp, state.result_file);
}

static void reset_test(void)
{
    state.step = 0;
    state.detected = 0;
    state.speed_mbps = 0;
    state.started = 1;
    state.completed = 0;
    state.failed = 0;
    clock_gettime(CLOCK_MONOTONIC, &state.step_started);
    snprintf(state.phase, sizeof(state.phase), "waiting_insert");
    set_result_path();
    unlink(state.result_file);
    snprintf(state.message, sizeof(state.message), "Insert %s device into %s (%s)", speed_name(0), port_name(0), direction_name(0));
}

static void poll_usb(void)
{
    if (!state.started || state.completed || state.failed) return;
    state.detected = detect_usb(&state.speed_mbps);
    if (!state.detected) {
        snprintf(state.phase, sizeof(state.phase), "waiting_insert");
        snprintf(state.message, sizeof(state.message), "Insert %s device into %s (%s)", speed_name(state.step), port_name(state.step), direction_name(state.step));
    } else {
        snprintf(state.phase, sizeof(state.phase), "detected");
        snprintf(state.message, sizeof(state.message), "USB detected at %d Mbps; confirm %s insertion", state.speed_mbps, direction_name(state.step));
    }
}

static void state_json(char *buffer, size_t size)
{
    struct timespec now;
    long elapsed_ms;
    const char *next_action;
    clock_gettime(CLOCK_MONOTONIC, &now);
    elapsed_ms = (long)(now.tv_sec - state.step_started.tv_sec) * 1000L +
                 (now.tv_nsec - state.step_started.tv_nsec) / 1000000L;
    next_action = state.completed ? "USB测试完成" : state.detected ? "确认当前插入方向" : "插入U盘";
    snprintf(buffer, size, "{\"mode\":\"%s\",\"currentStep\":%d,\"totalSteps\":8,\"usbVersion\":\"%s\",\"port\":\"%s\",\"direction\":\"%s\",\"phase\":\"%s\",\"detected\":%s,\"device\":\"%s\",\"speedMbps\":%d,\"elapsedMs\":%ld,\"started\":%s,\"completed\":%s,\"failed\":%s,\"result\":\"%s\",\"message\":\"%s\",\"nextAction\":\"%s\",\"resultFile\":\"%s\"}",
             state.mode, state.step + 1, speed_name(state.step), port_name(state.step), direction_name(state.step),
             state.phase, state.detected ? "true" : "false", state.detected ? "/dev/sd*" : "", state.speed_mbps,
             elapsed_ms, state.started ? "true" : "false", state.completed ? "true" : "false", state.failed ? "true" : "false",
             state.completed ? "passed" : state.failed ? "failed" : "pending", state.message, next_action, state.result_file);
}

static void response(int fd, const char *type, const char *body)
{
    dprintf(fd, "HTTP/1.1 200 OK\r\nContent-Type: %s\r\nContent-Length: %zu\r\nConnection: close\r\n\r\n%s", type, strlen(body), body);
}

static const char usb_page[] =
    "<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'>"
    "<meta name='viewport' content='width=device-width,initial-scale=1'>"
    "<title>Space USB口测试</title><style>"
    ":root{font-family:Segoe UI,Arial,sans-serif;color:#172033;background:#eef3f8}"
    "body{margin:0;padding:10px;min-width:1000px;min-height:748px}.shell{width:100%;max-width:1024px;min-height:748px;margin:auto;background:white;border-radius:10px;box-shadow:0 12px 40px #15223820;overflow:hidden}"
    ".top{padding:30px 38px;background:linear-gradient(120deg,#12365b,#176b87);color:white}.top h1{margin:0;font-size:42px}.top p{margin:10px 0 0;font-size:18px;color:#d8edf4}"
    ".content{padding:32px 38px}.grid{display:grid;grid-template-columns:1.25fr .75fr;gap:26px}.card{border:1px solid #d9e2ec;border-radius:12px;padding:26px;background:#fbfdff}"
    ".label{font-size:13px;color:#66758a;text-transform:uppercase;letter-spacing:.08em}.hero{font-size:30px;font-weight:700;margin:8px 0 14px}.hint{font-size:20px;line-height:1.5;margin:12px 0}.pill{display:inline-block;border-radius:99px;padding:7px 13px;font-weight:700;background:#e7eef7}.ok{background:#dcf7e8;color:#087443}.bad{background:#ffe4e6;color:#b42318}.active{background:#dbeafe;color:#175cd3}"
    ".progress{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-top:12px}.step{padding:10px 6px;text-align:center;border-radius:8px;background:#edf1f5;font-size:12px}.step.current{background:#dbeafe;color:#175cd3;font-weight:700}.step.done{background:#dcf7e8;color:#087443}.step.fail{background:#ffe4e6;color:#b42318}"
    ".facts{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin-top:14px}.fact b{display:block;font-size:20px;margin-top:3px}.actions{display:flex;gap:10px;flex-wrap:wrap;margin-top:20px}button{border:0;border-radius:9px;padding:12px 18px;font-weight:700;font-size:15px;color:white;background:#1769aa;cursor:pointer}button.secondary{background:#667085}button.danger{background:#b42318}"
    "@media(max-width:700px){body{padding:10px}.content,.top{padding:20px}.grid{grid-template-columns:1fr}.top h1{font-size:27px}.hero{font-size:25px}}</style></head>"
    "<body><main class='shell'><header class='top'><h1>Space USB口测试</h1><p>PCBA USB2.0 / USB3.0 联通性检测</p></header>"
    "<section class='content'><div class='grid'><article class='card'><div class='label'>当前测试</div><div id='hero' class='hero'>等待开始</div><span id='phase' class='pill'>未开始</span><p id='hint' class='hint'>请选择测试模式并开始 USB 测试。</p><div class='facts'><div class='fact'><span class='label'>设备</span><b id='device'>-</b></div><div class='fact'><span class='label'>实际速率</span><b id='speed'>-</b></div><div class='fact'><span class='label'>检测耗时</span><b id='elapsed'>0 秒</b></div><div class='fact'><span class='label'>下一步</span><b id='next'>-</b></div></div><div class='actions'><button onclick=post('/api/usb/start','pcba')>开始 PCBA 测试</button><button onclick=post('/api/usb/start','finished_product')>开始整机测试</button><button onclick=post('/api/usb/confirm-direction')>确认当前插入方向</button><button class='secondary' onclick=post('/api/usb/retry')>重试</button><button class='danger' onclick=post('/api/usb/abort')>终止</button></div></article>"
    "<aside class='card'><div class='label'>测试进度</div><h2 id='progressTitle'>0 / 8</h2><div id='progress' class='progress'></div><p id='file' class='label' style='margin-top:22px;word-break:break-all'></p></aside></div></section></main>"
    "<script>const $=id=>document.getElementById(id);async function post(url,mode){let o={method:'POST'};if(mode)o={...o,headers:{'Content-Type':'application/json'},body:JSON.stringify({mode})};await fetch(url,o);refresh()}"
    "function cls(p){return p==='passed'?'ok':p==='failed'?'bad':p==='detected'?'active':''}function refresh(){fetch('/api/usb/state').then(r=>r.json()).then(x=>{let n=x.currentStep||0; $('hero').textContent=x.started?(x.usbVersion.toUpperCase()+' '+x.port+' '+x.direction):'等待开始';$('phase').textContent=x.phase;$('phase').className='pill '+cls(x.result);$('hint').textContent=x.message||'等待开始';$('device').textContent=x.device||'-';$('speed').textContent=x.speedMbps?(x.speedMbps+' Mbps'):'-';$('elapsed').textContent=Math.max(0,Math.round((x.elapsedMs||0)/100)/10)+' 秒';$('next').textContent=x.nextAction||'-';$('progressTitle').textContent=n+' / '+(x.totalSteps||8);$('file').textContent=x.resultFile||'';let h='';for(let i=1;i<=8;i++){let c=i<n?'done':i===n?'current':'';h+='<div class=\"step '+c+'\">'+i+'</div>'}$('progress').innerHTML=h})}setInterval(refresh,500);refresh();</script></body></html>";

static void http_client(int fd)
{
    char request[4096], body[4096];
    ssize_t length = read(fd, request, sizeof(request) - 1);
    if (length <= 0) return;
    request[length] = '\0';
    pthread_mutex_lock(&state.lock);
    poll_usb();
    if (strncmp(request, "GET /usb-test", sizeof("GET /usb-test") - 1) == 0) {
        response(fd, "text/html; charset=utf-8", usb_page);
    } else if (strncmp(request, "GET /api/usb/state", sizeof("GET /api/usb/state") - 1) == 0 || strncmp(request, "GET /api/usb/result", sizeof("GET /api/usb/result") - 1) == 0) {
        state_json(body, sizeof(body)); response(fd, "application/json", body);
    } else if (strncmp(request, "POST /api/usb/start", sizeof("POST /api/usb/start") - 1) == 0) {
        snprintf(state.mode, sizeof(state.mode), strstr(request, "finished_product") ? "finished_product" : "pcba"); reset_test(); state_json(body, sizeof(body)); response(fd, "application/json", body);
    } else if (strncmp(request, "POST /api/usb/retry", sizeof("POST /api/usb/retry") - 1) == 0) {
        state.failed = 0; state.detected = 0; state.message[0] = '\0'; poll_usb(); state_json(body, sizeof(body)); response(fd, "application/json", body);
    } else if (strncmp(request, "POST /api/usb/confirm-direction", sizeof("POST /api/usb/confirm-direction") - 1) == 0) {
        if (state.started && state.detected) {
            int speed_ok = state.step < 4 ? state.speed_mbps < 5000 : state.speed_mbps >= 5000;
            if (!speed_ok) {
                state.failed = 1;
                snprintf(state.message, sizeof(state.message), "Wrong USB speed %d Mbps for %s step; retry", state.speed_mbps, speed_name(state.step));
                write_result();
            } else {
                state.step++;
                state.detected = 0;
                if (state.step >= USB_STEP_COUNT) { state.completed = 1; snprintf(state.message, sizeof(state.message), "USB pretest completed"); }
                else snprintf(state.message, sizeof(state.message), "Insert %s device into %s (%s)", speed_name(state.step), port_name(state.step), direction_name(state.step));
                write_result();
            }
        }
        state_json(body, sizeof(body)); response(fd, "application/json", body);
    } else if (strncmp(request, "POST /api/usb/abort", sizeof("POST /api/usb/abort") - 1) == 0) {
        state.failed = 1; snprintf(state.message, sizeof(state.message), "USB pretest aborted"); write_result(); state_json(body, sizeof(body)); response(fd, "application/json", body);
    } else {
        dprintf(fd, "HTTP/1.1 404 Not Found\r\nContent-Length: 9\r\nConnection: close\r\n\r\nNot Found");
    }
    pthread_mutex_unlock(&state.lock);
}

static void *worker(void *unused)
{
    int listener, one = 1;
    struct sockaddr_in address;
    (void)unused;
    listener = socket(AF_INET, SOCK_STREAM, 0);
    if (listener < 0) return NULL;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    memset(&address, 0, sizeof(address)); address.sin_family = AF_INET; address.sin_port = htons((uint16_t)state.port); inet_pton(AF_INET, "127.0.0.1", &address.sin_addr);
    if (bind(listener, (struct sockaddr *)&address, sizeof(address)) != 0 || listen(listener, 4) != 0) { close(listener); return NULL; }
    for (;;) {
        fd_set readable;
        struct timeval timeout = { .tv_sec = 0, .tv_usec = 500000 };
        FD_ZERO(&readable);
        FD_SET(listener, &readable);
        if (select(listener + 1, &readable, NULL, NULL, &timeout) > 0 && FD_ISSET(listener, &readable)) {
            int client = accept(listener, NULL, NULL);
            if (client >= 0) { http_client(client); close(client); }
        }
        pthread_mutex_lock(&state.lock);
        poll_usb();
        pthread_mutex_unlock(&state.lock);
    }
}

int usb_pretest_start(const struct app_config *config)
{
    if (config != NULL && !config->usb_pretest_enabled) return 0;
    if (state.running) return 0;
    state.running = 1;
    if (config != NULL && config->usb_pretest_http_port > 0) state.port = config->usb_pretest_http_port;
    create_desktop_shortcut();
    return pthread_create(&state.thread, NULL, worker, NULL);
}
void usb_pretest_stop(void) { }
