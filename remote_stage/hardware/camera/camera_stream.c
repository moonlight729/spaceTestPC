#define _POSIX_C_SOURCE 200809L
#include "camera_stream.h"

#include <errno.h>
#include <fcntl.h>
#include <linux/videodev2.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <unistd.h>

#define CAMERA_BUFFER_COUNT 4

struct camera_buffer {
    void *start;
    size_t length;
};

static void copy_text(char *dst, size_t dst_size, const char *src)
{
    size_t length;
    if (dst_size == 0) return;
    if (src == NULL) src = "";
    length = strnlen(src, dst_size - 1);
    memcpy(dst, src, length);
    dst[length] = '\0';
}

static void set_message(struct camera_stream_result *result, int code, const char *message)
{
    result->error_code = code;
    copy_text(result->message, sizeof(result->message), message);
}

static int xioctl(int fd, unsigned long request, void *arg)
{
    int rc;
    do {
        rc = ioctl(fd, request, arg);
    } while (rc < 0 && errno == EINTR);
    return rc;
}

static int read_int_file(const char *path, int *value)
{
    FILE *file;
    if (path == NULL || value == NULL) {
        errno = EINVAL;
        return -1;
    }
    file = fopen(path, "r");
    if (file == NULL) return -1;
    if (fscanf(file, "%d", value) != 1) {
        fclose(file);
        errno = EIO;
        return -1;
    }
    fclose(file);
    return 0;
}

static void unmap_buffers(struct camera_buffer *buffers, unsigned int count)
{
    unsigned int i;
    for (i = 0; i < count; ++i) {
        if (buffers[i].start != MAP_FAILED && buffers[i].start != NULL) {
            munmap(buffers[i].start, buffers[i].length);
        }
        buffers[i].start = NULL;
        buffers[i].length = 0;
    }
}

static int prepare_buffers(int fd, struct camera_buffer *buffers, unsigned int *mapped_count)
{
    struct v4l2_requestbuffers req;
    unsigned int i;
    memset(&req, 0, sizeof(req));
    req.count = CAMERA_BUFFER_COUNT;
    req.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    req.memory = V4L2_MEMORY_MMAP;
    if (xioctl(fd, VIDIOC_REQBUFS, &req) != 0 || req.count < 2) return -1;
    for (i = 0; i < req.count && i < CAMERA_BUFFER_COUNT; ++i) {
        struct v4l2_buffer buf;
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        buf.index = i;
        if (xioctl(fd, VIDIOC_QUERYBUF, &buf) != 0) {
            unmap_buffers(buffers, i);
            return -1;
        }
        buffers[i].length = buf.length;
        buffers[i].start = mmap(NULL, buf.length, PROT_READ | PROT_WRITE,
                                MAP_SHARED, fd, buf.m.offset);
        if (buffers[i].start == MAP_FAILED) {
            unmap_buffers(buffers, i);
            return -1;
        }
        if (xioctl(fd, VIDIOC_QBUF, &buf) != 0) {
            unmap_buffers(buffers, i + 1);
            return -1;
        }
    }
    *mapped_count = i;
    return 0;
}

static int capture_frames(int fd, int frame_count, int timeout_ms, int *captured_frames)
{
    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    struct pollfd poll_fd;
    int captured = 0;
    if (xioctl(fd, VIDIOC_STREAMON, &type) != 0) return -1;
    while (captured < frame_count) {
        struct v4l2_buffer buf;
        int rc;
        memset(&poll_fd, 0, sizeof(poll_fd));
        poll_fd.fd = fd;
        poll_fd.events = POLLIN;
        rc = poll(&poll_fd, 1, timeout_ms);
        if (rc <= 0) {
            xioctl(fd, VIDIOC_STREAMOFF, &type);
            errno = rc == 0 ? ETIMEDOUT : errno;
            return -1;
        }
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        if (xioctl(fd, VIDIOC_DQBUF, &buf) != 0) {
            xioctl(fd, VIDIOC_STREAMOFF, &type);
            return -1;
        }
        ++captured;
        if (xioctl(fd, VIDIOC_QBUF, &buf) != 0) {
            xioctl(fd, VIDIOC_STREAMOFF, &type);
            return -1;
        }
    }
    xioctl(fd, VIDIOC_STREAMOFF, &type);
    *captured_frames = captured;
    return 0;
}

int camera_stream_run_test(const struct camera_stream_request *request,
                           struct camera_stream_result *result)
{
    struct v4l2_capability cap;
    struct camera_buffer buffers[CAMERA_BUFFER_COUNT];
    unsigned int mapped_count = 0;
    int fd;
    int exposure_before = 0;
    int exposure_after = 0;
    int i;

    if (request == NULL || result == NULL || request->device_path == NULL ||
        request->stream_frame_count <= 0 || request->timeout_ms <= 0) {
        errno = EINVAL;
        return -1;
    }
    memset(result, 0, sizeof(*result));
    copy_text(result->device_path, sizeof(result->device_path), request->device_path);
    for (i = 0; i < CAMERA_BUFFER_COUNT; ++i) {
        buffers[i].start = NULL;
        buffers[i].length = 0;
    }

    if (request->require_exposure_interrupt &&
        read_int_file(request->exposure_counter_path, &exposure_before) != 0) {
        set_message(result, 4700, "Unable to read camera exposure counter before streaming");
        return -1;
    }

    fd = open(request->device_path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) {
        set_message(result, 4701, "Unable to open camera video device");
        return -1;
    }
    if (xioctl(fd, VIDIOC_QUERYCAP, &cap) != 0 ||
        !(cap.capabilities & V4L2_CAP_VIDEO_CAPTURE) ||
        !(cap.capabilities & V4L2_CAP_STREAMING)) {
        close(fd);
        set_message(result, 4702, "Camera device does not support capture streaming");
        return -1;
    }
    if (prepare_buffers(fd, buffers, &mapped_count) != 0) {
        close(fd);
        set_message(result, 4703, "Unable to prepare camera streaming buffers");
        return -1;
    }
    if (capture_frames(fd, request->stream_frame_count, request->timeout_ms,
                       &result->captured_frames) != 0) {
        unmap_buffers(buffers, mapped_count);
        close(fd);
        set_message(result, 4704, "Unable to capture camera stream frames");
        return -1;
    }
    unmap_buffers(buffers, mapped_count);
    close(fd);
    result->stream_ok = true;

    if (request->require_exposure_interrupt) {
        if (read_int_file(request->exposure_counter_path, &exposure_after) != 0) {
            set_message(result, 4705, "Unable to read camera exposure counter after streaming");
            return -1;
        }
        result->exposure_delta = exposure_after - exposure_before;
        result->exposure_ok = result->exposure_delta >= request->exposure_frame_count;
        if (!result->exposure_ok) {
            set_message(result, 4706, "Camera exposure interrupt count is below requirement");
            return -1;
        }
    }

    set_message(result, 0, "Camera stream check passed");
    return 0;
}
