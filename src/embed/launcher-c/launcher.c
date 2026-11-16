// THE SMALL EXE, IN C: a port of `src\embed\launcher\Program.cs`, and the launcher the product ships.
//
// WHAT IT IS: a SHORTCUT onto the engine's own entry point. It holds NO engine code, decides NOTHING about its
// arguments, and loads `ScriptLangCore.dll` from its own folder, so `ScriptLangCore.Launcher.exe <args>` does what
// `ScriptLangCore.exe <args>` does: no arguments opens the REPL, a `.slc` runs, a `.slcbc` runs, `--compile`,
// `--dump-bytecode`, `--repl [script]` and `--mcp` all work, because the argument list is handed to the engine's own
// dispatch (`slc_main`) rather than reinterpreted here. Nothing in this file can drift from the exe's behaviour, and a
// new flag is inherited the day it lands.
//
// WHY IT IS C: the C# version is 1,185,280 B of NativeAOT runtime for a program whose own work is a few hundred lines
// (a DLL load, ten export lookups, a resource read and two string builders). A C launcher is the same program with no
// runtime behind it, so it costs tens of kilobytes instead of a megabyte.
//
// USAGE: ScriptLangCore.Launcher.exe [the engine's own arguments, exactly]
//
// WHAT IT DOES, in order: load the DLL beside itself (explaining how to get one if it is missing), start a session with
// that folder as the engine's `home` (its `assets\`, `tools\` and name-bound natives such as `sqlite3.dll` live there),
// register an output callback that writes channel 0 to stdout and channel 1 to stderr, hand its own arguments to
// `slc_main`, and exit with the code that answered. It sets NO session arguments of its own: `Application.Args` then
// reads this process's command line, which is what the exe's `Application.Args` reads too.
//
// THE PORT IS DELIBERATELY BEHAVIOUR-IDENTICAL, message for message, because `tools/embed-check.sh` pins the words AND
// the exit codes of every refusal, and because a packaged app's stderr is what a user reads. The two places it does
// something the C# does not are both about failing WELL rather than crashing: an export that the loaded DLL does not
// have is a refusal naming it (the C# version threw an unhandled EntryPointNotFoundException there), and the load
// failure prints the Win32 error instead of a CLR exception message.
//
// There are no em dashes anywhere in this file, on purpose (the repository's rule).

#define WIN32_LEAN_AND_MEAN
// THE WIDE WIN32 API, stated here as well as by the build's `-municode` (which already defines both). Guarded rather than
// plain `#define`, because a redefinition is an error under `-Werror`, and deleted would mean the file no longer works if
// that flag went away: `wmain` and every `...W` call below depend on it.
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <shellapi.h>   // CommandLineToArgvW, which the entry point uses in place of the C runtime's parser
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

// THE ENGINE'S FILE NAME, and the one name this program knows. Its folder is also the engine's HOME (`assets\`, `tools\`
// and `sqlite3.dll` are published beside it).
#define SLC_DLL_NAME L"ScriptLangCore.dll"

// THE TWO BUNDLE MAGICS, as the wire writes them (a little-endian int32 at offset 0). Both are spelled with their byte
// readings beside them, because the names mislead: 0x43424353 reads "SCBC" and 0x4C424C53 reads "SLBL". The test is the
// INTEGER, which is how the wire writes it and how the engine's own `slc_run_file` tests a file: a byte comparison here
// silently classified a `.slcbc` as source text once.
#define BUNDLE_PROGRAM_MAGIC 0x43424353
#define BUNDLE_LIBRARY_MAGIC 0x4C424C53

// THE PAYLOAD SLOT IN THIS EXE'S OWN RESOURCE. `tools/slc-pack.slc` fills it with a script's text or a bundle's bytes;
// size 0 means "not packaged", which is why the packer creates the entry rather than requiring it to exist.
#define PAYLOAD_RESOURCE_TYPE 10 /* RT_RCDATA */
#define PAYLOAD_RESOURCE_NAME L"SLCPAYLOAD"

// THE THREE REFUSALS' TITLES, which are also the box captions a double-clicked package shows.
#define TITLE_NO_DLL L"ScriptLangCore: the engine DLL is missing"
#define TITLE_LOAD_FAILED L"ScriptLangCore: the engine DLL will not load"
#define TITLE_NO_SESSION L"ScriptLangCore: the engine refused to start"

// A refusal exits with 3, the engine's own `ExitCode.Refused`, which is what `tools/embed-check.sh` pins.
#define EXIT_REFUSED 3

// `MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST`, and a timeout so a window nobody is there to dismiss cannot hang a
// machine: it closes itself, and a user dismisses it sooner with Enter.
#define MESSAGE_FLAGS 0x00000010u | 0x00010000u | 0x00040000u
#define MESSAGE_TIMEOUT_MS 20000

// ── THE ENGINE'S C ABI, as `src\Host\EmbedApi.cs` exports it ────────────────────────────────────────────────────────
// Handles are the engine's int64 session ids, so `long long` and not `long` (which is 32 bits on Windows).
typedef int (*slc_version_fn)(unsigned char* buffer, int capacity);
typedef long long (*slc_create_fn)(const char* optionsJson);
typedef int (*slc_free_fn)(long long handle);
typedef int (*slc_set_output_fn)(long long handle, void* callback, void* user);
typedef int (*slc_main_fn)(long long handle, const char* argvJson);
typedef int (*slc_main_owns_fn)(const char* argumentUtf8);
typedef int (*slc_run_text_fn)(long long handle, const char* sourceUtf8, const char* nameUtf8);
typedef int (*slc_run_bundle_bytes_fn)(long long handle, const char* data, int count);
typedef int (*slc_buffer_fn)(long long handle, unsigned char* buffer, int capacity);
typedef void (*slc_line_cb)(long long handle, int channel, const char* utf8, void* user);

// ── THE RUNTIME THIS LAUNCHER DOES NOT CARRY ──────────────────────────────────────────────────────────────────────
// The image is linked WITHOUT the C runtime (`-nostartfiles -nostdlib`), which MEASURED 31,744 -> 18,944 B and costs this
// section: each function here was a libc call. Two mingw facts forced the call sites to be RENAMED rather than the
// functions redefined: its headers make `iswspace` a MACRO and declare `_wcsicmp` as `dllimport`, so a definition of
// either is expanded away or ignored and the call still lands in msvcrt. And `-fno-builtin` is required with them,
// because gcc otherwise recognises these loops as the libc functions they replace and calls them anyway.
//
// WHAT IS GIVEN UP, stated because it is a real trade: the CRT's startup installs an unhandled-exception filter, so a
// fault inside the launcher now ends with Windows' own report instead of a message. Nothing in this program catches a
// fault itself, every reachable failure is CHECKED (a load, an export, a resource, an ABI call), and the crash a user
// would hit is a bug in this file rather than a condition to report.
static void write_error_text(const char* text);
static void out_of_memory(void);

static void* xmalloc(size_t size)
{
    void* memory = HeapAlloc(GetProcessHeap(), 0, size == 0 ? 1 : size);
    if (memory == NULL) out_of_memory();
    return memory;
}

static void* xrealloc(void* memory, size_t size)
{
    void* grown = memory == NULL
        ? HeapAlloc(GetProcessHeap(), 0, size == 0 ? 1 : size)
        : HeapReAlloc(GetProcessHeap(), 0, memory, size == 0 ? 1 : size);
    if (grown == NULL) out_of_memory();
    return grown;
}

// `free(NULL)` was a no-op in the CRT, and 60-odd call sites rely on that.
static void xfree(void* memory)
{
    if (memory != NULL) HeapFree(GetProcessHeap(), 0, memory);
}

static void copy_bytes(void* destination, const void* source, size_t count)
{
    unsigned char* to = (unsigned char*)destination;
    const unsigned char* from = (const unsigned char*)source;
    while (count-- > 0) { *to++ = *from++; }
}

static size_t text_length(const char* text)
{
    size_t count = 0;
    while (text[count] != '\0') count++;
    return count;
}

static size_t wide_length(const wchar_t* text)
{
    size_t count = 0;
    while (text[count] != L'\0') count++;
    return count;
}

static wchar_t* wide_find_char(const wchar_t* text, wchar_t wanted)
{
    for (; *text != L'\0'; text++) { if (*text == wanted) return (wchar_t*)text; }
    return wanted == L'\0' ? (wchar_t*)text : NULL;
}

static int wide_same(const wchar_t* left, const wchar_t* right)
{
    while (*left != L'\0' && *left == *right) { left++; right++; }
    return (int)*left - (int)*right;
}

// A PATH COMPARISON folds the ASCII and Latin-1 letters and nothing else, which is all a Windows file system folds. The
// one caller asks whether the chosen engine IS the one beside the exe, so the safe direction is to answer "not the same"
// about two spellings above Latin-1 and load the declared one rather than skip the announcement.
static wchar_t fold_letter(wchar_t letter)
{
    if (letter >= L'A' && letter <= L'Z') return (wchar_t)(letter + 32);
    if (letter >= 0xC0 && letter <= 0xDE && letter != 0xD7) return (wchar_t)(letter + 32);
    return letter;
}

static int same_path_ci(const wchar_t* left, const wchar_t* right)
{
    for (;; left++, right++)
    {
        wchar_t a = fold_letter(*left);
        wchar_t b = fold_letter(*right);
        if (a != b) return (int)a - (int)b;
        if (a == L'\0') return 0;
    }
}

static int is_blank_char(wchar_t letter)
{
    return letter == L' ' || (letter >= 0x09 && letter <= 0x0D);
}

// THE `PATH` WALK `wcstok_s` used to do: the next entry, split in place on the separators, with the state the caller owns.
static wchar_t* next_path_entry(wchar_t* text, const wchar_t* separators, wchar_t** state)
{
    wchar_t* at = text != NULL ? text : *state;
    if (at == NULL) return NULL;
    while (*at != L'\0' && wide_find_char(separators, *at) != NULL) at++;
    if (*at == L'\0') { *state = at; return NULL; }
    wchar_t* start = at;
    while (*at != L'\0' && wide_find_char(separators, *at) == NULL) at++;
    if (*at != L'\0') { *at = L'\0'; *state = at + 1; } else { *state = at; }
    return start;
}

// `exit` flushed the CRT's stdio and then ended the process; this launcher writes through `WriteFile`, so there is nothing
// to flush and the exit is the process's.
static void leave(int code)
{
    ExitProcess((UINT)code);
}

static void write_error_text(const char* text)
{
    DWORD written = 0;
    WriteFile(GetStdHandle(STD_ERROR_HANDLE), text, (DWORD)text_length(text), &written, NULL);
}

static void out_of_memory(void)
{
    // A launcher that cannot allocate cannot say anything useful, and silence here reads as "the app does nothing".
    write_error_text("launcher: out of memory\r\n");
    leave(EXIT_REFUSED);
}


// ── A BYTE BUFFER, which stands in for the `StringBuilder` and the string concatenations of the C# version ─────────
typedef struct
{
    char* data;
    size_t length;
    size_t capacity;
} Buffer;

static void buffer_init(Buffer* buffer)
{
    buffer->data = NULL;
    buffer->length = 0;
    buffer->capacity = 0;
}

static void buffer_reserve(Buffer* buffer, size_t extra)
{
    if (buffer->length + extra + 1 <= buffer->capacity) return;
    size_t wanted = buffer->capacity == 0 ? 256 : buffer->capacity;
    while (wanted < buffer->length + extra + 1) wanted *= 2;
    char* grown = (char*)xrealloc(buffer->data, wanted);
    if (grown == NULL) out_of_memory();
    buffer->data = grown;
    buffer->capacity = wanted;
}

static void buffer_add_bytes(Buffer* buffer, const char* text, size_t length)
{
    buffer_reserve(buffer, length);
    if (length > 0) copy_bytes(buffer->data + buffer->length, text, length);
    buffer->length += length;
    buffer->data[buffer->length] = '\0';
}

static void buffer_add(Buffer* buffer, const char* text)
{
    buffer_add_bytes(buffer, text, text_length(text));
}

static void buffer_add_char(Buffer* buffer, char value)
{
    buffer_add_bytes(buffer, &value, 1);
}

static void buffer_add_wide_n(Buffer* buffer, const wchar_t* text, size_t length)
{
    if (length == 0) return;
    int needed = WideCharToMultiByte(CP_UTF8, 0, text, (int)length, NULL, 0, NULL, NULL);
    if (needed <= 0) return;
    buffer_reserve(buffer, (size_t)needed);
    WideCharToMultiByte(CP_UTF8, 0, text, (int)length, buffer->data + buffer->length, needed, NULL, NULL);
    buffer->length += (size_t)needed;
    buffer->data[buffer->length] = '\0';
}

static void buffer_add_wide(Buffer* buffer, const wchar_t* text)
{
    buffer_add_wide_n(buffer, text, wide_length(text));
}

static void buffer_free(Buffer* buffer)
{
    xfree(buffer->data);
    buffer_init(buffer);
}

static wchar_t* wide_from_utf8(const char* utf8)
{
    int needed = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    if (needed <= 0) return NULL;
    wchar_t* text = (wchar_t*)xmalloc((size_t)needed * sizeof(wchar_t));
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, text, needed);
    return text;
}

static char* utf8_from_wide(const wchar_t* text)
{
    int needed = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (needed <= 0) return NULL;
    char* utf8 = (char*)xmalloc((size_t)needed);
    WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, needed, NULL, NULL);
    return utf8;
}

// ── WRITING TO A STANDARD STREAM ────────────────────────────────────────────────────────────────────────────────────
// TWO DOORS, because a console and a pipe want different bytes: a CONSOLE takes UTF-16 through WriteConsoleW, which is
// what .NET's `Console` does and what makes an accented name or an emoji survive whatever code page the shell was
// started with; a REDIRECTED or piped stream takes the raw UTF-8 a reader parses. `Console.Out` (which the C# version
// writes through) does exactly this, so a script's `print` relayed here lands as the same bytes either way.
static void write_std(DWORD which, const char* utf8, size_t length)
{
    if (length == 0) return;
    HANDLE stream = GetStdHandle(which);
    DWORD mode = 0;
    if (stream != NULL && stream != INVALID_HANDLE_VALUE && GetConsoleMode(stream, &mode))
    {
        int wideCount = MultiByteToWideChar(CP_UTF8, 0, utf8, (int)length, NULL, 0);
        if (wideCount > 0)
        {
            wchar_t* wide = (wchar_t*)xmalloc((size_t)wideCount * sizeof(wchar_t));
            if (MultiByteToWideChar(CP_UTF8, 0, utf8, (int)length, wide, wideCount) == wideCount)
            {
                DWORD written = 0;
                WriteConsoleW(stream, wide, (DWORD)wideCount, &written, NULL);
            }
            xfree(wide);
        }
        return;
    }
    if (stream != NULL && stream != INVALID_HANDLE_VALUE)
    {
        DWORD written = 0;
        WriteFile(stream, utf8, (DWORD)length, &written, NULL);
    }
}

// One line, terminated the way `Console.WriteLine` terminates one on Windows. Nothing is buffered on this side, so the
// launcher's lines and the engine's own writes reach the reader in the order they happened.
static void write_line(DWORD which, const char* utf8, size_t length)
{
    Buffer line;
    buffer_init(&line);
    buffer_add_bytes(&line, utf8, length);
    buffer_add(&line, "\r\n");
    write_std(which, line.data, line.length);
    buffer_free(&line);
}

static void stderr_line_utf8(const char* utf8)
{
    write_line(STD_ERROR_HANDLE, utf8, text_length(utf8));
}

// ── DECIMALS AND HEX WITHOUT A FORMATTER ──────────────────────────────────────────────────────────────────────────
// Three call sites used `snprintf` (a `\u%04x` in the JSON escaper, a `%lu` GetLastError suffix and a `%d` status), and
// asking the C runtime for a format string linked its whole formatted-output machinery: MEASURED, replacing those three
// calls with these two helpers took the image from 57,856 to 31,744 B, so 26 KB of printf came with them. A launcher
// that prints four hex digits and a decimal has no business carrying one, and the text is byte-identical (the escapes
// and the refusals are what `tools/embed-check.sh` reads).
static char* put_decimal(char* at, unsigned long value)
{
    char digits[24];
    int count = 0;
    if (value == 0) { *at++ = '0'; return at; }
    while (value > 0 && count < (int)sizeof digits) { digits[count++] = (char)('0' + (int)(value % 10)); value /= 10; }
    while (count > 0) { *at++ = digits[--count]; }
    return at;
}
static char* put_hex4(char* at, unsigned value)
{
    static const char nibbles[] = "0123456789abcdef";
    for (int shift = 12; shift >= 0; shift -= 4) { *at++ = nibbles[(value >> shift) & 0xFu]; }
    return at;
}
static char* put_text(char* at, const char* text)
{
    while (*text != '\0') { *at++ = *text++; }
    return at;
}

// ── PATHS ───────────────────────────────────────────────────────────────────────────────────────────────────────────
// These follow the `System.IO.Path` members the C# version calls, including the detail that a DIRECTORY from
// `AppContext.BaseDirectory` KEEPS its trailing separator: one refusal prints that value, and the two spellings differ.
static int wide_is_blank(const wchar_t* text)
{
    if (text == NULL) return 1;
    for (const wchar_t* p = text; *p != L'\0'; p++)
    {
        if (!is_blank_char(*p)) return 0;
    }
    return 1;
}

static wchar_t* environment_dup(const wchar_t* name)
{
    DWORD needed = GetEnvironmentVariableW(name, NULL, 0);
    if (needed == 0) return NULL;
    wchar_t* value = (wchar_t*)xmalloc((size_t)needed * sizeof(wchar_t));
    DWORD copied = GetEnvironmentVariableW(name, value, needed);
    if (copied == 0 || copied >= needed)
    {
        xfree(value);
        return NULL;
    }
    return value;
}

// THE IMAGE'S OWN PATH, from `GetModuleFileNameW(NULL, ...)`: our own file, whatever it is called and wherever it sits.
static wchar_t* module_path(void)
{
    DWORD capacity = 512;
    for (;;)
    {
        wchar_t* path = (wchar_t*)xmalloc((size_t)capacity * sizeof(wchar_t));
        DWORD copied = GetModuleFileNameW(NULL, path, capacity);
        if (copied == 0)
        {
            xfree(path);
            return NULL;
        }
        if (copied < capacity - 1)
        {
            return path;
        }
        // Truncated: a longer buffer, twice the size, and read again.
        xfree(path);
        capacity *= 2;
        if (capacity > 32768 * 4) return NULL;
    }
}

// The folder of a path, WITH the trailing separator, which is the shape `AppContext.BaseDirectory` has.
static wchar_t* directory_of(const wchar_t* path)
{
    size_t length = wide_length(path);
    // First the file name goes: everything after the last separator. Without this step the "folder" is the path itself,
    // which is the bug this line exists for (measured: the launcher then looked for `...exe\ScriptLangCore.dll` and
    // reported that the DLL was not beside it while it sat right there).
    while (length > 0 && path[length - 1] != L'\\' && path[length - 1] != L'/') length--;
    // Then any separator run goes, except the one that makes a drive root: "D:\" is a folder, "D:" is not.
    while (length > 1 && (path[length - 1] == L'\\' || path[length - 1] == L'/'))
    {
        if (length == 3 && path[1] == L':') break;
        length--;
    }
    if (length == 0) return NULL;
    wchar_t* folder = (wchar_t*)xmalloc((length + 2) * sizeof(wchar_t));
    copy_bytes(folder, path, length * sizeof(wchar_t));
    folder[length] = L'\0';
    if (folder[length - 1] != L'\\' && folder[length - 1] != L'/')
    {
        folder[length] = L'\\';
        folder[length + 1] = L'\0';
    }
    return folder;
}

// The same folder WITHOUT its trailing separator, which is the shape `Path.GetDirectoryName` answers and the shape the
// session's `home` takes. A drive root keeps its separator, because "D:" alone means "the current folder on D:".
static wchar_t* directory_no_separator(const wchar_t* path)
{
    wchar_t* folder = directory_of(path);
    if (folder == NULL) return NULL;
    size_t length = wide_length(folder);
    if (length > 1 && (folder[length - 1] == L'\\' || folder[length - 1] == L'/'))
    {
        if (!(length == 3 && folder[1] == L':')) folder[length - 1] = L'\0';
    }
    return folder;
}

// `Path.Combine`, for the two shapes this program needs: a folder (with or without its separator) plus a file name.
static wchar_t* path_combine(const wchar_t* folder, const wchar_t* name)
{
    size_t folderLength = wide_length(folder);
    size_t nameLength = wide_length(name);
    int needsSeparator = folderLength > 0 && folder[folderLength - 1] != L'\\' && folder[folderLength - 1] != L'/';
    wchar_t* combined = (wchar_t*)xmalloc((folderLength + nameLength + 2) * sizeof(wchar_t));
    copy_bytes(combined, folder, folderLength * sizeof(wchar_t));
    size_t at = folderLength;
    if (needsSeparator) combined[at++] = L'\\';
    copy_bytes(combined + at, name, (nameLength + 1) * sizeof(wchar_t));
    return combined;
}

// `Path.GetFullPath`: normalises separators and resolves a relative path against the PROCESS's current directory.
static wchar_t* full_path_dup(const wchar_t* path)
{
    DWORD capacity = 512;
    for (;;)
    {
        wchar_t* full = (wchar_t*)xmalloc((size_t)capacity * sizeof(wchar_t));
        DWORD copied = GetFullPathNameW(path, capacity, full, NULL);
        if (copied == 0)
        {
            xfree(full);
            return NULL;
        }
        if (copied < capacity)
        {
            return full;
        }
        xfree(full);
        capacity = copied + 1;
    }
}

// `File.Exists`, which answers FALSE for a directory: a folder named `ScriptLangCore.dll` is not a DLL to load.
static int file_exists(const wchar_t* path)
{
    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES) return 0;
    return (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

static void* load_export(HMODULE library, const char* name)
{
    return (void*)GetProcAddress(library, name);
}

// ── JSON ───────────────────────────────────────────────────────────────────────────────────────────────────────────
// `Escape` from the C# version, on the UTF-8 bytes: identical for every character below 0x20 and for the five escapes,
// and every other character travels as its own bytes, which is what `Marshal.StringToCoTaskMemUTF8` produced.
static void append_json_string(Buffer* out, const wchar_t* value)
{
    buffer_add_char(out, '"');
    char* utf8 = utf8_from_wide(value);
    if (utf8 != NULL)
    {
        for (const unsigned char* p = (const unsigned char*)utf8; *p != '\0'; p++)
        {
            unsigned char c = *p;
            switch (c)
            {
                case '"': buffer_add(out, "\\\""); break;
                case '\\': buffer_add(out, "\\\\"); break;
                case '\n': buffer_add(out, "\\n"); break;
                case '\r': buffer_add(out, "\\r"); break;
                case '\t': buffer_add(out, "\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        char escape[7];
                        escape[0] = '\\';
                        escape[1] = 'u';
                        escape[6] = '\0';
                        put_hex4(escape + 2, (unsigned)c);
                        buffer_add(out, escape);
                    }
                    else
                    {
                        buffer_add_char(out, (char)c);
                    }
                    break;
            }
        }
        xfree(utf8);
    }
    buffer_add_char(out, '"');
}

// ── ONE DOOR FOR AN EARLY REFUSAL ───────────────────────────────────────────────────────────────────────────────────
// A PACKAGED APP HAS NO CONSOLE, so an early refusal has to be SAID IN A WINDOW: a package started without its DLL used
// to exit 3 and show nothing at all, which reads as a program that does nothing. The words go to stderr FIRST and
// always (a console run, a script and `tools/embed-check.sh`, whose legs pin exit 3 plus the file name, read them), and
// a box follows when nobody would ever see those words. THREE SHAPES decide that, and the middle one is why this is not
// simply "a box when no console is attached":
//   * a SHELL shares its console with this process (GetConsoleProcessList counts more than one), so the lines stay on
//     screen: no box, which is also what keeps an unattended run from blocking;
//   * a DOUBLE-CLICKED CONSOLE IMAGE gets a console of its OWN (the count is one) and that console disappears with the
//     process, so the lines are unreadable: box;
//   * NO CONSOLE AT ALL means either a GUI image started from Explorer (box) or a run whose streams are CAPTURED, and
//     then GetFileType answers pipe or disk and the reader sees the text (no box).
static int fatal_box_wanted(void)
{
    if (GetConsoleWindow() != NULL)
    {
        // 1 means this process is ALONE on the console, so Windows created it for us and it dies with us. 0 means the
        // call failed, and a box is the safer answer than silence.
        HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
        typedef DWORD(WINAPI * get_console_process_list_fn)(DWORD* list, DWORD count);
        get_console_process_list_fn list = kernel == NULL ? NULL : (get_console_process_list_fn) load_export(kernel, "GetConsoleProcessList");
        if (list == NULL) return 1;
        DWORD pids[8];
        return list(pids, 8) <= 1 ? 1 : 0;
    }
    // FILE_TYPE_PIPE is 3 and FILE_TYPE_DISK is 1: either way something is reading stderr.
    DWORD type = GetFileType(GetStdHandle(STD_ERROR_HANDLE));
    return (type != FILE_TYPE_PIPE && type != FILE_TYPE_DISK) ? 1 : 0;
}

static void show_message_box(const wchar_t* caption, const wchar_t* text)
{
    HMODULE user = LoadLibraryW(L"user32.dll");
    if (user == NULL) return;
    typedef int(WINAPI * message_box_timeout_fn)(HWND window, LPCWSTR body, LPCWSTR title, UINT type, WORD languageId, DWORD milliseconds);
    message_box_timeout_fn box = (message_box_timeout_fn) load_export(user, "MessageBoxTimeoutW");
    if (box != NULL)
    {
        box(NULL, text, caption, MESSAGE_FLAGS, 0, MESSAGE_TIMEOUT_MS);
    }
    FreeLibrary(user);
}

static void fatal(const wchar_t* caption, const wchar_t* text)
{
    // One "launcher: " line per line of the message, right-trimmed, which is what the C# version writes: a refusal's
    // TEXT is the paragraph a user reads, and the prefix is what tells them which program is talking.
    const wchar_t* at = text;
    for (;;)
    {
        const wchar_t* newline = wide_find_char(at, L'\n');
        size_t length = newline == NULL ? wide_length(at) : (size_t)(newline - at);
        while (length > 0 && is_blank_char(at[length - 1])) length--;
        Buffer line;
        buffer_init(&line);
        buffer_add(&line, "launcher: ");
        buffer_add_wide_n(&line, at, length);
        write_line(STD_ERROR_HANDLE, line.data, line.length);
        buffer_free(&line);
        if (newline == NULL) break;
        at = newline + 1;
    }
    if (fatal_box_wanted())
    {
        show_message_box(caption, text);
    }
}

// `Ask`: the `snprintf` convention the ABI documents, one probe for the length then one fill.
static char* ask_buffer(slc_buffer_fn call, long long handle)
{
    int needed = call(handle, NULL, 0);
    if (needed <= 0) return NULL;
    char* buffer = (char*)xmalloc((size_t)needed + 1);
    buffer[0] = '\0';
    if (call(handle, (unsigned char*)buffer, needed + 1) < 0) buffer[0] = '\0';
    buffer[needed] = '\0';
    return buffer;
}

// ── THE OUTPUT CALLBACK ────────────────────────────────────────────────────────────────────────────────────────────
// Channel 0 is the script's `print` and channel 1 is the engine's own log lines; the callback runs on the session's
// thread, and a REPL's prompt and `= value` echoes go to the process's stdout on their own, which is parity with the exe.
static void line_sink(long long handle, int channel, const char* utf8, void* user)
{
    (void)handle;
    (void)user;
    if (utf8 == NULL) utf8 = "";
    write_line(channel == 0 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE, utf8, text_length(utf8));
}

// ── THE PAYLOAD IN THIS EXE'S OWN RESOURCE ─────────────────────────────────────────────────────────────────────────
// Read through Win32: `tools/slc-pack.slc` writes `SLCPAYLOAD` (RT_RCDATA, language neutral) into a COPY of this exe,
// and an absent entry and an empty one both mean "not packaged", so neither the reader nor the packer has to ask
// whether the slot exists.
static unsigned char* read_own_payload(DWORD* payloadLength)
{
    *payloadLength = 0;
    HMODULE self = GetModuleHandleW(NULL);
    if (self == NULL) return NULL;
    HRSRC info = FindResourceW(self, PAYLOAD_RESOURCE_NAME, MAKEINTRESOURCEW(PAYLOAD_RESOURCE_TYPE));
    if (info == NULL) return NULL;
    DWORD size = SizeofResource(self, info);
    if (size == 0) return NULL;
    HGLOBAL loaded = LoadResource(self, info);
    if (loaded == NULL) return NULL;
    const void* data = LockResource(loaded);
    if (data == NULL) return NULL;
    // A COPY, because the resource lives in the image's READ-ONLY pages and the ABI's `byte*` is a mutable pointer: a
    // body that wrote through it would fault on a page the loader will not unlock.
    unsigned char* copy = (unsigned char*)xmalloc(size);
    copy_bytes(copy, data, size);
    *payloadLength = size;
    return copy;
}

int wmain(int argc, wchar_t** argv);

int wmain(int argc, wchar_t** argv)
{
    wchar_t* baseDirectory = NULL;
    {
        wchar_t* self = module_path();
        if (self != NULL)
        {
            baseDirectory = directory_of(self);
            xfree(self);
        }
        if (baseDirectory == NULL)
        {
            // No image path at all (a pathological host): the folder is then empty, and every refusal still names the
            // file it looked for rather than printing nothing.
            baseDirectory = (wchar_t*)xmalloc(sizeof(wchar_t));
            baseDirectory[0] = L'\0';
        }
    }

    // ── WHERE THE ENGINE IS, in four steps, most explicit first ────────────────────────────────────────────────────
    // `SLC_ENGINE_DLL` (an exact file) then BESIDE THIS EXE (which comes BEFORE the environment on purpose: an installer
    // sets `SLC_ENGINE_HOME` machine-wide, so an app that ships its own DLL was once made to run the INSTALLED engine,
    // silently changing the version it was built against), then `SLC_ENGINE_HOME` (a directory), then each `PATH` entry
    // in order. A DECLARED location that does not hold the engine is REFUSED rather than skipped: a typo must not
    // silently load a different engine than the one the user named.
    wchar_t* declaredFile = environment_dup(L"SLC_ENGINE_DLL");
    wchar_t* declaredHome = environment_dup(L"SLC_ENGINE_HOME");
    wchar_t* engineDll = NULL;

    if (!wide_is_blank(declaredFile))
    {
        if (!file_exists(declaredFile))
        {
            Buffer text;
            buffer_init(&text);
            buffer_add(&text, "This app needs ScriptLangCore.dll, and SLC_ENGINE_DLL names '");
            buffer_add_wide(&text, declaredFile);
            buffer_add(&text, "', which does not exist.\r\n\r\nA declared location is obeyed rather than skipped, so "
                              "this app will not fall back to another copy while SLC_ENGINE_DLL is set.\r\n\r\n");
            // A STALE VARIABLE IS THE COMMON SHAPE OF THIS REFUSAL, not a typo: an uninstall (or a moved folder) leaves
            // the value naming a file nothing recreates, and the old wording ended by telling the user to do the one
            // thing it could not know was already done: "copy ScriptLangCore.dll beside this exe", in a folder that held
            // one. Reported from a live run whose engine sat beside the exe the whole time, blocked by a `C:\seed\...`
            // value its shell had inherited from before the uninstall. So the answer depends on what IS there, and the
            // refusal itself (a declared location is obeyed, not consulted) plus the exit code are unchanged.
            wchar_t* beside = path_combine(baseDirectory, SLC_DLL_NAME);
            if (file_exists(beside))
            {
                buffer_add(&text, "There IS a ScriptLangCore.dll beside this exe (");
                buffer_add_wide(&text, beside);
                buffer_add(&text, "), so the variable is all that stops it: clear SLC_ENGINE_DLL in the Environment "
                                  "Variables dialog and start a new console, or point it at the DLL you mean.");
            }
            else
            {
                buffer_add(&text, "Point SLC_ENGINE_DLL at a DLL that exists, or clear it and put ScriptLangCore.dll "
                                  "beside this exe (");
                buffer_add_wide(&text, baseDirectory);
                buffer_add(&text, ").");
            }
            xfree(beside);
            wchar_t* message = wide_from_utf8(text.data);
            fatal(TITLE_NO_DLL, message);
            xfree(message);
            xfree(declaredFile);
            xfree(declaredHome);
            xfree(baseDirectory);
            return EXIT_REFUSED;
        }
        engineDll = full_path_dup(declaredFile);
    }
    else
    {
        wchar_t* beside = path_combine(baseDirectory, SLC_DLL_NAME);
        if (file_exists(beside))
        {
            engineDll = full_path_dup(beside);
        }
        else if (!wide_is_blank(declaredHome))
        {
            wchar_t* candidate = path_combine(declaredHome, SLC_DLL_NAME);
            if (!file_exists(candidate))
            {
                Buffer text;
                buffer_init(&text);
                buffer_add(&text, "This app needs ScriptLangCore.dll. There is none beside this exe, and SLC_ENGINE_HOME "
                                  "names '");
                buffer_add_wide(&text, declaredHome);
                buffer_add(&text, "', which holds no ScriptLangCore.dll.\r\n\r\nPoint SLC_ENGINE_HOME at the folder that "
                                  "holds the DLL, with its assets\\, tools\\ and sqlite3.dll beside it.");
                wchar_t* message = wide_from_utf8(text.data);
                fatal(TITLE_NO_DLL, message);
                xfree(message);
                xfree(candidate);
                xfree(beside);
                xfree(declaredFile);
                xfree(declaredHome);
                xfree(baseDirectory);
                return EXIT_REFUSED;
            }
            engineDll = full_path_dup(candidate);
            xfree(candidate);
        }
        else
        {
            // PATH, in its own order, so a directory the user declared there wins over another one further along it.
            wchar_t* pathList = environment_dup(L"PATH");
            if (pathList != NULL)
            {
                wchar_t* context = NULL;
                for (wchar_t* entry = next_path_entry(pathList, L";", &context); entry != NULL && engineDll == NULL;
                     entry = next_path_entry(NULL, L";", &context))
                {
                    // `raw.Trim().Trim('"')`: whitespace first, then the quotes a hand-edited PATH carries.
                    wchar_t* start = entry;
                    while (*start != L'\0' && is_blank_char(*start)) start++;
                    size_t length = wide_length(start);
                    while (length > 0 && is_blank_char(start[length - 1])) length--;
                    while (length > 0 && start[0] == L'"')
                    {
                        start++;
                        length--;
                    }
                    while (length > 0 && start[length - 1] == L'"') length--;
                    start[length] = L'\0';
                    if (length == 0) continue;
                    wchar_t* folder = full_path_dup(start);
                    if (folder == NULL) continue; // an entry that is not a usable path is skipped, as Path.GetFullPath's throw is
                    wchar_t* candidate = path_combine(folder, SLC_DLL_NAME);
                    if (file_exists(candidate))
                    {
                        engineDll = full_path_dup(candidate);
                    }
                    xfree(candidate);
                    xfree(folder);
                }
                xfree(pathList);
            }
        }
        xfree(beside);
    }

    if (engineDll == NULL)
    {
        Buffer text;
        buffer_init(&text);
        buffer_add(&text, "This app is packaged around ScriptLangCore.dll, and that file is not there.\r\n\r\n"
                          "Looked for it:\r\n  ");
        wchar_t* beside = path_combine(baseDirectory, SLC_DLL_NAME);
        buffer_add_wide(&text, beside);
        xfree(beside);
        buffer_add(&text, "\r\n  the SLC_ENGINE_DLL / SLC_ENGINE_HOME you set (none was set)\r\n"
                          "  every folder on PATH\r\n\r\n"
                          "Copy ScriptLangCore.dll beside this exe, with its assets\\, tools\\ and sqlite3.dll, or set "
                          "SLC_ENGINE_HOME to the folder that holds it.");
        wchar_t* message = wide_from_utf8(text.data);
        fatal(TITLE_NO_DLL, message);
        xfree(message);
        xfree(declaredFile);
        xfree(declaredHome);
        xfree(baseDirectory);
        return EXIT_REFUSED;
    }

    // THE ENGINE'S HOME IS THE FOLDER ITS DLL CAME FROM, not this exe's: that folder is where its `assets\`, `tools\` and
    // `sqlite3.dll` were published, so a shared or PATH-found engine still finds its own files. It is the shape
    // `Path.GetDirectoryName` answers, i.e. WITHOUT a trailing separator, which is also what the session's `home` takes.
    wchar_t* engineFolder = directory_no_separator(engineDll);
    {
        // The announcement a shared engine earns: printed only when the DLL did NOT come from beside this exe, which is
        // the unusual case it exists for (and it is what tells a stale DLL from a fresh one when a report looks wrong).
        wchar_t* beside = path_combine(baseDirectory, SLC_DLL_NAME);
        wchar_t* besideFull = full_path_dup(beside);
        wchar_t* chosenFull = full_path_dup(engineDll);
        int sameAsBeside = besideFull != NULL && chosenFull != NULL && same_path_ci(besideFull, chosenFull) == 0;
        if (!sameAsBeside)
        {
            Buffer line;
            buffer_init(&line);
            buffer_add(&line, "launcher: engine from ");
            buffer_add_wide(&line, engineDll);
            write_line(STD_ERROR_HANDLE, line.data, line.length);
            buffer_free(&line);
        }
        xfree(besideFull);
        xfree(chosenFull);
        xfree(beside);
    }

    // ── THE DLL ITSELF ──────────────────────────────────────────────────────────────────────────────────────────
    HMODULE library = LoadLibraryW(engineDll);
    if (library == NULL)
    {
        // A DLL that is there but will not load (another architecture, a truncated copy, a blocked file) used to end the
        // process as an unhandled exception, which a packaged app shows as nothing at all.
        DWORD error = GetLastError();
        wchar_t detail[512];
        detail[0] = L'\0';
        if (FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, error,
                           MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT), detail, (DWORD)(sizeof detail / sizeof detail[0]), NULL) == 0)
        {
            detail[0] = L'\0';
        }
        while (detail[0] != L'\0')
        {
            size_t length = wide_length(detail);
            if (length > 0 && is_blank_char(detail[length - 1]))
            {
                detail[length - 1] = L'\0';
                continue;
            }
            break;
        }
        Buffer text;
        buffer_init(&text);
        buffer_add(&text, "ScriptLangCore.dll is at ");
        buffer_add_wide(&text, engineDll);
        buffer_add(&text, ", and Windows refused to load it.\r\n\r\n");
        buffer_add_wide(&text, detail);
        {
            char code[48];
            char* at = put_text(code, " (GetLastError() = ");
            at = put_decimal(at, (unsigned long)error);
            *at++ = ')';
            *at = '\0';
            buffer_add(&text, code);
        }
        wchar_t* message = wide_from_utf8(text.data);
        fatal(TITLE_LOAD_FAILED, message);
        xfree(message);
        xfree(engineFolder);
        xfree(engineDll);
        xfree(declaredFile);
        xfree(declaredHome);
        xfree(baseDirectory);
        return EXIT_REFUSED;
    }

    // THE TEN EXPORTS THIS FILE CALLS. A name the loaded DLL does not have is a refusal naming it, where the C# version
    // threw an unhandled EntryPointNotFoundException (a stack trace, and no exit code a caller could act on).
    slc_version_fn slc_version = (slc_version_fn) load_export(library, "slc_version");
    slc_create_fn slc_create = (slc_create_fn) load_export(library, "slc_create");
    slc_free_fn slc_free = (slc_free_fn) load_export(library, "slc_free");
    slc_set_output_fn slc_set_output = (slc_set_output_fn) load_export(library, "slc_set_output");
    slc_main_fn slc_main = (slc_main_fn) load_export(library, "slc_main");
    slc_main_owns_fn slc_main_owns = (slc_main_owns_fn) load_export(library, "slc_main_owns");
    slc_run_text_fn slc_run_text = (slc_run_text_fn) load_export(library, "slc_run_text");
    slc_run_bundle_bytes_fn slc_run_bundle_bytes = (slc_run_bundle_bytes_fn) load_export(library, "slc_run_bundle_bytes");
    slc_buffer_fn slc_last_error = (slc_buffer_fn) load_export(library, "slc_last_error");
    slc_buffer_fn slc_last_result = (slc_buffer_fn) load_export(library, "slc_last_result");
    if (slc_version == NULL || slc_create == NULL || slc_free == NULL || slc_set_output == NULL || slc_main == NULL ||
        slc_main_owns == NULL || slc_run_text == NULL || slc_run_bundle_bytes == NULL || slc_last_error == NULL ||
        slc_last_result == NULL)
    {
        Buffer text;
        buffer_init(&text);
        buffer_add(&text, "The engine at ");
        buffer_add_wide(&text, engineDll);
        buffer_add(&text, " does not export the whole C ABI this launcher calls (one of slc_version, slc_create, "
                          "slc_free, slc_set_output, slc_main, slc_main_owns, slc_run_text, slc_run_bundle_bytes, "
                          "slc_last_error, slc_last_result). It is a different or older engine than this launcher was "
                          "built against.");
        wchar_t* message = wide_from_utf8(text.data);
        fatal(TITLE_LOAD_FAILED, message);
        xfree(message);
        FreeLibrary(library);
        xfree(engineFolder);
        xfree(engineDll);
        xfree(declaredFile);
        xfree(declaredHome);
        xfree(baseDirectory);
        return EXIT_REFUSED;
    }

    // THE ENGINE SAYS NOTHING ABOUT ITSELF BY DEFAULT, because a packaged app's stderr belongs to the app: this line used
    // to print on every run and a reader reported it as noise (it is also invisible in a double-clicked GUI app). Asking
    // for it is one variable, the same one the verdict line at the bottom uses, so "the launcher talks about itself" has
    // ONE switch rather than two that could drift.
    wchar_t* informativeSetting = environment_dup(L"SLC_LAUNCHER_INFO");
    int informative = !wide_is_blank(informativeSetting) && wide_same(informativeSetting, L"0") != 0 &&
                      wide_same(informativeSetting, L"false") != 0;
    xfree(informativeSetting);

    if (informative)
    {
        int needed = slc_version(NULL, 0);
        if (needed > 0)
        {
            char* version = (char*)xmalloc((size_t)needed + 1);
            version[0] = '\0';
            slc_version((unsigned char*)version, needed + 1);
            version[needed] = '\0';
            Buffer line;
            buffer_init(&line);
            buffer_add(&line, "launcher: ");
            buffer_add(&line, version);
            write_line(STD_ERROR_HANDLE, line.data, line.length);
            buffer_free(&line);
            xfree(version);
        }
    }

    // ── THE SESSION ────────────────────────────────────────────────────────────────────────────────────────────
    // `home` is the ENGINE's folder and nothing else is set: the arguments stay this process's own, so a script's
    // `Application.Args` reads what the exe's would read (the command line minus the executable). `args` is this
    // process's OWN argument list, verbatim: leaving it out would mean NO arguments, which is a different thing.
    int scriptArgc = argc > 1 ? argc - 1 : 0;
    Buffer options;
    buffer_init(&options);
    buffer_add(&options, "{\"home\":\"");
    {
        // Only the backslashes are doubled, which is what the C# version does: a Windows path cannot contain a quote.
        char* folderUtf8 = utf8_from_wide(engineFolder);
        if (folderUtf8 != NULL)
        {
            for (const char* p = folderUtf8; *p != '\0'; p++)
            {
                if (*p == '\\') buffer_add(&options, "\\\\");
                else buffer_add_char(&options, *p);
            }
            xfree(folderUtf8);
        }
    }
    buffer_add(&options, "\",\"args\":[");
    for (int i = 0; i < scriptArgc; i++)
    {
        if (i > 0) buffer_add_char(&options, ',');
        append_json_string(&options, argv[i + 1]);
    }
    buffer_add(&options, "]}");

    long long handle = slc_create(options.data);
    buffer_free(&options);

    if (handle <= 0)
    {
        char* reason = ask_buffer(slc_last_error, 0);
        Buffer text;
        buffer_init(&text);
        buffer_add(&text, "The engine is at ");
        buffer_add_wide(&text, engineDll);
        buffer_add(&text, ", and it refused to start a session:\r\n\r\n");
        buffer_add(&text, reason == NULL ? "" : reason);
        wchar_t* message = wide_from_utf8(text.data);
        fatal(TITLE_NO_SESSION, message);
        xfree(message);
        xfree(reason);
        FreeLibrary(library);
        xfree(engineFolder);
        xfree(engineDll);
        xfree(declaredFile);
        xfree(declaredHome);
        xfree(baseDirectory);
        return EXIT_REFUSED;
    }

    slc_set_output(handle, (void*)line_sink, NULL);

    // WHAT AM I? A PACKAGED exe carries its program in its OWN resource, put there by `tools/slc-pack.slc`, and that is
    // the one thing this file decides for itself: is there a payload. THE RULE, in one line: a payload runs, UNLESS the
    // first argument is one the ENGINE's own dispatch owns (`--compile`, `--repl`, `--mcp`, `--dump-bytecode`,
    // `--mcp-bc`), which then takes over exactly as it would on the unpacked launcher. Asking the engine means the list
    // has ONE statement, in the engine, and a flag added there works in a packaged app the day it lands.
    DWORD payloadLength = 0;
    unsigned char* payload = read_own_payload(&payloadLength);

    int wantsEngine = 0;
    if (scriptArgc > 0)
    {
        for (int i = 0; i < scriptArgc && !wantsEngine; i++)
        {
            char* candidate = utf8_from_wide(argv[i + 1]);
            if (candidate != NULL)
            {
                if (slc_main_owns(candidate) == 1) wantsEngine = 1;
                xfree(candidate);
            }
        }
    }
    else
    {
        // No arguments at all: the same question about the empty string, which is what the C# version asks.
        wantsEngine = slc_main_owns("") == 1;
    }

    int status;
    if (payload != NULL && payloadLength > 0 && !wantsEngine)
    {
        // A PAYLOAD IS RUN BY WHAT IT IS, read from its first four bytes and never from a name it does not have: the two
        // bundle magics mean "compiled", anything else is the SCRIPT'S TEXT. The run's directory is this exe's folder,
        // so `./` means "beside the exe".
        unsigned int magic = 0;
        if (payloadLength >= 4)
        {
            magic = (unsigned int)payload[0] | ((unsigned int)payload[1] << 8) | ((unsigned int)payload[2] << 16) |
                    ((unsigned int)payload[3] << 24);
        }
        if (magic == (unsigned int)BUNDLE_PROGRAM_MAGIC || magic == (unsigned int)BUNDLE_LIBRARY_MAGIC)
        {
            status = slc_run_bundle_bytes(handle, (const char*)payload, (int)payloadLength);
        }
        else
        {
            // Source text: NUL-terminated UTF-8, and the exe's own path is handed over as the run's NAME so
            // `Script.Directory()` and every `./` path point at the folder the packaged exe sits in.
            char* source = (char*)xmalloc((size_t)payloadLength + 1);
            copy_bytes(source, payload, payloadLength);
            source[payloadLength] = '\0';
            wchar_t* selfPath = module_path();
            char* name = selfPath == NULL ? NULL : utf8_from_wide(selfPath);
            status = slc_run_text(handle, source, name == NULL ? "" : name);
            xfree(name);
            xfree(selfPath);
            xfree(source);
        }
    }
    else
    {
        // THE ARGUMENTS, as one JSON array: this is the only thing this file knows about a command line.
        Buffer argvJson;
        buffer_init(&argvJson);
        buffer_add_char(&argvJson, '[');
        for (int i = 0; i < scriptArgc; i++)
        {
            if (i > 0) buffer_add_char(&argvJson, ',');
            append_json_string(&argvJson, argv[i + 1]);
        }
        buffer_add_char(&argvJson, ']');
        status = slc_main(handle, argvJson.data);
        buffer_free(&argvJson);
    }
    xfree(payload);

    // THE VERDICT IS THE RUN'S EXIT CODE, exactly as the CLI's is, and the JSON is an OPTION (the `SLC_LAUNCHER_INFO`
    // switch the version line uses). It used to be written unconditionally, which was WRONG for a host that runs a
    // script which CHOOSES its code: measured, a suite's child (`exit(3)`) put two stderr lines on a child the suite
    // asserts is silent.
    if (status != 0 && informative)
    {
        char* reason = ask_buffer(slc_last_error, handle);
        Buffer line;
        buffer_init(&line);
        buffer_add(&line, "launcher: the run answered ");
        {
            char number[16];
            char* at = number;
            unsigned long magnitude = (unsigned long)status;
            if (status < 0) { *at++ = '-'; magnitude = (unsigned long)(-(long long)status); }
            at = put_decimal(at, magnitude);
            *at = '\0';
            buffer_add(&line, number);
        }
        buffer_add(&line, ": ");
        buffer_add(&line, reason == NULL ? "" : reason);
        write_line(STD_ERROR_HANDLE, line.data, line.length);
        buffer_free(&line);
        xfree(reason);

        char* result = ask_buffer(slc_last_result, handle);
        if (result != NULL)
        {
            stderr_line_utf8(result);
        }
        xfree(result);
    }
    slc_free(handle);
    FreeLibrary(library);
    xfree(engineFolder);
    xfree(engineDll);
    xfree(declaredFile);
    xfree(declaredHome);
    xfree(baseDirectory);
    return status;
}

// ── THE ENTRY POINT, which the C runtime used to provide ──────────────────────────────────────────────────────────
// `-e wmainCRTStartup` names this, so the runtime's own startup (and its ~6 KB of initialisation) is not in the image.
// The command line is split by `CommandLineToArgvW`, which is the splitter Windows itself uses and therefore the one a
// double-click and a `cmd` line both go through; `tools/embed-check.sh` pins it with a quoted argument carrying a space.
// `argv[0]` is the exe's path, exactly as the CRT's was, and this launcher reads only from `argv[1]` on.
void wmainCRTStartup(void)
{
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    int status = wmain(argc, argv);
    ExitProcess((UINT)status);
}
