/* Run against the actual Windows Nt*File implementation with a synthetic save. */
#include "kernel.h"
#include <stdio.h>
#include <string.h>

static WCHAR fixture_path[MAX_PATH];

BOOL xbox_translate_path(const char *path, xbox_host_char *out, DWORD count)
{
    (void)path;
    if (wcslen(fixture_path) + 1 > count)
        return FALSE;
    wcscpy_s(out, count, fixture_path);
    return TRUE;
}

void xbox_log(int level, const char *subsystem, const char *fmt, ...)
{
    (void)level;
    (void)subsystem;
    (void)fmt;
}

static HANDLE open_save(char *path, ULONG disposition)
{
    XBOX_ANSI_STRING name = { (USHORT)strlen(path), (USHORT)(strlen(path) + 1), path };
    XBOX_OBJECT_ATTRIBUTES attrs = { NULL, &name, 0 };
    XBOX_IO_STATUS_BLOCK status = { 0 };
    HANDLE h = NULL;
    if (!NT_SUCCESS(xbox_NtCreateFile(&h, XBOX_GENERIC_READ | XBOX_GENERIC_WRITE,
            &attrs, &status, NULL, 0, 0, disposition,
            XBOX_FILE_SYNCHRONOUS_IO_NONALERT)))
        return NULL;
    return h;
}

static int put(HANDLE h, void *bytes, ULONG count)
{
    XBOX_IO_STATUS_BLOCK status = { 0 };
    return h && NT_SUCCESS(xbox_NtWriteFile(h, NULL, NULL, NULL, &status, bytes, count, NULL))
             && status.Information == count;
}

int main(void)
{
    enum { BODY = 2792, SIGNATURE = 20 };
    unsigned char original[BODY], signature[SIGNATURE], result[BODY + SIGNATURE];
    WCHAR temp[MAX_PATH];
    HANDLE h = NULL;
    XBOX_IO_STATUS_BLOCK status = { 0 };
    XBOX_FILE_POSITION_INFORMATION position;
    DWORD count;
    int rc = 1;

    if (!GetTempPathW(MAX_PATH, temp) || !GetTempFileNameW(temp, L"sbq", 0, fixture_path))
        return 1;

    memset(original, 0x5A, sizeof original);
    memset(signature, 0xC3, sizeof signature);
    h = open_save("T:\\pilot.sav", XBOX_FILE_OVERWRITE_IF);
    if (!put(h, original, sizeof original))
        goto done;
    xbox_NtClose(h);
    h = open_save("T:\\pilot.sav", XBOX_FILE_OVERWRITE_IF);
    position.CurrentByteOffset.QuadPart = BODY;
    if (!h || !NT_SUCCESS(xbox_NtSetInformationFile(h, &status, &position,
            sizeof position, XboxFilePositionInformation))
           || !put(h, signature, sizeof signature))
        goto done;
    xbox_NtClose(h);

    h = CreateFileW(fixture_path, GENERIC_READ, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE || !ReadFile(h, result, sizeof result, &count, NULL)
        || count != sizeof result || memcmp(result + BODY, signature, SIGNATURE))
        goto done;
    if (memcmp(result, original, BODY)) {
        puts("FAIL: signature exists, but the pilot body was lost");
        goto done;
    }
    CloseHandle(h);
    h = open_save("T:\\pilot.sav", XBOX_FILE_OVERWRITE);
    if (!put(h, signature, SIGNATURE) || GetFileSize(h, NULL) != SIGNATURE)
        goto done;
    xbox_NtClose(h);
    h = open_save("T:\\pilot.sav", XBOX_FILE_OVERWRITE);
    if (!h)
        goto done;
    xbox_NtClose(h);
    h = open_save("T:\\pilot.sav", XBOX_FILE_OPEN);
    if (!h || GetFileSize(h, NULL) != 0)
        goto done;
    xbox_NtClose(h);

    /* A write with an explicit offset follows the same FATX prefix rule. */
    h = open_save("T:\\pilot.sav", XBOX_FILE_OVERWRITE_IF);
    if (!put(h, original, sizeof original))
        goto done;
    xbox_NtClose(h);
    h = open_save("T:\\pilot.sav", XBOX_FILE_OVERWRITE_IF);
    position.CurrentByteOffset.QuadPart = BODY;
    if (!h || !NT_SUCCESS(xbox_NtWriteFile(h, NULL, NULL, NULL, &status,
            signature, sizeof signature, &position.CurrentByteOffset)))
        goto done;
    xbox_NtClose(h);
    h = CreateFileW(fixture_path, GENERIC_READ, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE || !ReadFile(h, result, sizeof result, &count, NULL)
        || count != sizeof result || memcmp(result, original, BODY)
        || memcmp(result + BODY, signature, SIGNATURE))
        goto done;

    /* An overwrite on a non-FATX path retains the original Win32 behavior. */
    xbox_NtClose(h);
    h = open_save("D:\\pilot.sav", XBOX_FILE_OVERWRITE_IF);
    if (!h || GetFileSize(h, NULL) != 0)
        goto done;
    puts("PASS: signature rewrite preserves the pilot body, ordinary overwrites truncate");
    rc = 0;
done:
    if (h && h != INVALID_HANDLE_VALUE)
        xbox_NtClose(h);
    DeleteFileW(fixture_path);
    return rc;
}
