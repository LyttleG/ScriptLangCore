/* jitplugin_native.c: THE PLUGIN SIDE OF THE CONTRACT, WRITTEN IN C, so the native leg is proved by a plugin that
 * shares nothing with the host -- no CLR, no runtimeconfig, no .NET anything. `build.sh` compiles it with GCC x64 into
 * `bin/JitPluginNative.dll`, and the engine loads it with LoadLibrary because no `<name>.runtimeconfig.json` sits
 * beside it.
 *
 * THE CONTRACT IS THE SAME ONE THE C# FIXTURE SPEAKS (see `src/Host/JitPlugins/PluginContract.cs`):
 *
 *   { uint32 Abi; uint32 Size; Ping; Invoke }
 *
 * `Abi` is the contract version and `Size` is how much of the table this plugin filled: both come first because the
 * first field ever added would otherwise be an out-of-bounds WRITE in an older plugin. The host refuses another Abi,
 * a table shorter than it reads, and an empty verb door, BY NAME.
 *
 * THE DOOR'S FOUR RULES, all inherited from the engine's own C ABI:
 *   1. the payload is UTF-8 on every platform (NOT `char_t`: that is a Windows/.NET notion);
 *   2. the request is NUL-terminated and caller-owned, and this plugin keeps no pointer to it;
 *   3. an answer is LENGTH-DELIMITED with the `snprintf` convention: return the bytes needed, write at most
 *      `capacity` of them, and answer the same length when `capacity` is 0 or the buffer is NULL;
 *   4. a NEGATIVE return is a refusal, which the host reports as a status rather than reading as a length.
 *
 * WHAT THIS SAMPLE IS NOT: a JSON parser. It reads the four members the fixture needs (`m` and `p`'s `a`, `b`, `n`)
 * with a small scanner that trusts the request to be the object the Bridge contract defines, and it is deliberately
 * not a general parser -- a plugin that needs one links one. It also serves fewer verbs than the C# fixture: `add`,
 * `echo`, `long` and `utf8`, plus the `UnknownVerb` refusal, which is what `plugin_native_demo.slc` and the check's
 * probe ask for. The hostile-answer shapes (`bad`, `liar`, `oldversion`, `throw`) exist to test the HOST's reading,
 * and the C# fixture keeps them.
 *
 * NOTHING THROWS ACROSS, because C has nothing to throw: a failure here is an envelope with a code
 * (`PluginFailed`) or the plugin's refusal status, never a fault the host has to survive.
 */

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The table, restated here on purpose: the layout IS the contract, and a plugin that included the host's header
 * would be exercising a shared type rather than an ABI. Field ORDER and WIDTH are the wire. */
typedef struct PluginTable
{
    uint32_t Abi;
    uint32_t Size;
    int (*Ping)(unsigned char *answer, int capacity);
    int (*Invoke)(const unsigned char *request, unsigned char *answer, int capacity);
} PluginTable;

#define CONTRACT_ABI 1u
static const char kIdentity[] = "JitPluginNative (C) abi 1";

/* ── the door's length convention, in one place ─────────────────────────────────────────────────────────────── */

static int write_bytes(const char *text, size_t length, unsigned char *answer, int capacity)
{
    if (answer == NULL || capacity < (int)length) return (int)length;
    memcpy(answer, text, length);
    return (int)length;
}

/* ── the four members this fixture reads, by scanning: see WHAT THIS SAMPLE IS NOT ───────────────────────────── */

static const char *skip_ws(const char *p)
{
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
    return p;
}

/* The value of "name" (a quoted key, so `"a"` cannot match inside `"add"`), or NULL. A key that appears more than once
 * answers the first, which is what a reader of the contract expects. */
static const char *find_member(const char *json, const char *name)
{
    size_t want = strlen(name);
    const char *p = json;
    if (json == NULL) return NULL;
    while ((p = strchr(p, '"')) != NULL)
    {
        const char *key = p + 1;
        const char *end = strchr(key, '"');
        if (end == NULL) return NULL;
        if ((size_t)(end - key) == want && memcmp(key, name, want) == 0)
        {
            const char *value = skip_ws(end + 1);
            if (*value != ':') return NULL;
            return skip_ws(value + 1);
        }
        p = end + 1;
    }
    return NULL;
}

static int member_int(const char *json, const char *name)
{
    const char *value = find_member(json, name);
    return value == NULL ? 0 : (int)strtol(value, NULL, 10);
}

static void member_string(const char *json, const char *name, char *out, size_t capacity)
{
    const char *value = find_member(json, name);
    size_t i = 0;
    if (capacity > 0) out[0] = 0;
    if (value == NULL || *value != '"') return;
    for (value++; *value != 0 && *value != '"' && i + 1 < capacity; value++)
    {
        if (*value == '\\' && value[1] != 0) value++;   /* a verb is a plain name: an escape is skipped, not decoded */
        out[i++] = *value;
    }
    if (capacity > 0) out[i] = 0;
}

/* A name echoed into a message is sanitised, so a hostile verb cannot inject quotes into the envelope. */
static void sanitise(const char *text, char *out, size_t capacity)
{
    size_t i = 0;
    if (capacity > 0) out[0] = 0;
    for (; *text != 0 && i + 1 < capacity; text++)
    {
        char c = *text;
        int plain = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '.' || c == '-' || c == '_';
        out[i++] = plain ? c : '?';
    }
    if (capacity > 0) out[i] = 0;
}

/* ── the verbs ─────────────────────────────────────────────────────────────────────────────────────────────── */

static int answer_request(const char *request, unsigned char *answer, int capacity)
{
    char verb[64];
    char shown[64];
    char reply[4096];
    int a = member_int(request, "a");
    int b = member_int(request, "b");

    member_string(request, "m", verb, sizeof verb);

    if (strcmp(verb, "add") == 0)
        return write_bytes(reply, (size_t)snprintf(reply, sizeof reply,
                                                   "{\"v\":1,\"ok\":true,\"r\":%d}", a + b), answer, capacity);

    if (strcmp(verb, "echo") == 0)
        return write_bytes(reply, (size_t)snprintf(reply, sizeof reply,
                                                   "{\"v\":1,\"ok\":true,\"r\":\"echo:%d/%d\"}", a, b), answer, capacity);

    /* The accent and the emoji are written as BYTES, because the contract says UTF-8 and this file's own encoding
     * must not be the thing that decides. */
    if (strcmp(verb, "utf8") == 0)
        return write_bytes(reply, (size_t)snprintf(reply, sizeof reply,
                                                   "{\"v\":1,\"ok\":true,\"r\":\"caf\xc3\xa9" " et "
                                                   "\xf0\x9f\x91\x8b\"}"), answer, capacity);

    if (strcmp(verb, "long") == 0)
    {
        /* `long` answers n 'x' characters, which is how the host's 4000-byte-answer check is driven. The length is
         * computed BEFORE anything is written, so a probe with no buffer costs one strlen and no allocation. */
        static const char kHead[] = "{\"v\":1,\"ok\":true,\"r\":\"";
        static const char kTail[] = "\"}";
        int n = member_int(request, "n");
        size_t head = sizeof kHead - 1, tail = sizeof kTail - 1, total;
        size_t filled;
        if (n < 0) n = 0;
        if (n > 600000) n = 600000;                     /* a sample bounds its own answer */
        total = head + (size_t)n + tail;
        if (answer == NULL || capacity < (int)total) return (int)total;
        memcpy(answer, kHead, head);
        memset(answer + head, 'x', (size_t)n);
        memcpy(answer + head + n, kTail, tail);
        filled = total;
        return (int)filled;
    }

    sanitise(verb, shown, sizeof shown);
    return write_bytes(reply, (size_t)snprintf(reply, sizeof reply,
                                               "{\"v\":1,\"ok\":false,\"e\":{\"code\":\"UnknownVerb\","
                                               "\"message\":\"this C plugin serves add, echo, long and utf8, not '%s'\"}}",
                                               shown), answer, capacity);
}

/* ── the two slots ──────────────────────────────────────────────────────────────────────────────────────────── */

static int plugin_ping(unsigned char *answer, int capacity)
{
    return write_bytes(kIdentity, sizeof kIdentity - 1, answer, capacity);
}

static int plugin_invoke(const unsigned char *request, unsigned char *answer, int capacity)
{
    const char *text = request == NULL ? "" : (const char *)request;
    return answer_request(text, answer, capacity);
}

/* ── the one entry point, resolved by NAME (`GetProcAddress`) ───────────────────────────────────────────────── */

__declspec(dllexport) int PluginInit(PluginTable *table)
{
    if (table == NULL) return 1;                          /* 1: the plugin's own refusal to start */
    table->Abi = CONTRACT_ABI;
    table->Size = (uint32_t)sizeof(PluginTable);
    table->Ping = plugin_ping;
    table->Invoke = plugin_invoke;
    return 0;
}