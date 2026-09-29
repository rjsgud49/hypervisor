#pragma once

/* Include after ntddk.h (kernel) or Windows.h (user).
   The desktop frame lives in driver memory. VGA windows remain optional. */

#define HVSHARE_VERSION 1u
#define HVSHARE_MAX_PAYLOAD (32u * 1024u * 1024u)
#define HVSHARE_MAX_W 4096u
#define HVSHARE_MAX_H 2400u

#define HVSHARE_SOURCE_LFB 1u
#define HVSHARE_SOURCE_TEXT 2u
#define HVSHARE_SOURCE_LEGACY 3u
#define HVSHARE_SOURCE_DESKTOP 4u

#define HVSHARE_FORMAT_INDEX8 1u
#define HVSHARE_FORMAT_TEXT 2u
#define HVSHARE_FORMAT_BGRA32 3u

#define IOCTL_HVSHARE_QUERY CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_HVSHARE_READ CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_OUT_DIRECT, FILE_READ_DATA)
#define IOCTL_HVSHARE_PUBLISH CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_IN_DIRECT, FILE_WRITE_DATA)

#pragma pack(push, 1)
typedef struct _HVSHARE_INFO {
    unsigned long Version;
    unsigned long Source;
    unsigned long Format;
    unsigned long Width;
    unsigned long Height;
    unsigned long Pitch;
    unsigned long Payload;
    unsigned long PaletteScale;
    unsigned long long PhysBase;
    unsigned char Palette[768];
} HVSHARE_INFO, *PHVSHARE_INFO;

typedef struct _HVSHARE_PUBLISH {
    unsigned long Width;
    unsigned long Height;
    unsigned long Pitch;
    unsigned long Format;
} HVSHARE_PUBLISH, *PHVSHARE_PUBLISH;
#pragma pack(pop)
