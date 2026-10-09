#include <windows.h>
/* TinyCC's small Windows headers omit the NLS declaration. This is the standard Win32 ABI. */
WINBASEAPI int WINAPI WideCharToMultiByte(UINT, DWORD, LPCWSTR, int, LPSTR, int, LPCSTR, LPBOOL);
#define CP_UTF8 65001

/* Runs only in the message thread of the Player started and owned by Unidot. */
static LONG installed = 0;
static UINT unidot_message = 0;

typedef void* (__cdecl *domain_get_fn)(void);
typedef void* (__cdecl *assembly_open_fn)(void*, const char*);
typedef void* (__cdecl *assembly_image_fn)(void*);
typedef void* (__cdecl *class_find_fn)(void*, const char*, const char*);
typedef void* (__cdecl *method_find_fn)(void*, const char*, int);
typedef void* (__cdecl *invoke_fn)(void*, void*, void**, void**);
typedef void* (__cdecl *object_string_fn)(void*, void**);
typedef char* (__cdecl *string_utf8_fn)(void*);
typedef void (__cdecl *mono_free_fn)(void*);

static void report_error(const char* message)
{
    WCHAR path[32768];
    DWORD length = GetEnvironmentVariableW(L"UNIDOT_RUNTIME_DIRECTORY", path, 32700);
    if (length == 0 || length >= 32700) return;
    lstrcatW(path, L"\\runtime-error.txt");
    HANDLE file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written;
        WriteFile(file, message, (DWORD)lstrlenA(message), &written, NULL);
        CloseHandle(file);
    }
}

__declspec(dllexport) LRESULT CALLBACK UnidotRuntimeHook(int code, WPARAM wparam, LPARAM lparam)
{
    if (unidot_message == 0) unidot_message = RegisterWindowMessageW(L"Unidot.RuntimeBootstrap.4F524947494E414C.v1");
    if (code >= 0 && unidot_message != 0 && ((MSG*)lparam)->message == unidot_message)
    {
        ((MSG*)lparam)->message = WM_NULL;
        if (installed == 0)
        {
            HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
            if (mono != NULL)
            {
                domain_get_fn domain_get = (domain_get_fn)GetProcAddress(mono, "mono_domain_get");
                assembly_open_fn assembly_open = (assembly_open_fn)GetProcAddress(mono, "mono_domain_assembly_open");
                assembly_image_fn assembly_image = (assembly_image_fn)GetProcAddress(mono, "mono_assembly_get_image");
                class_find_fn class_find = (class_find_fn)GetProcAddress(mono, "mono_class_from_name");
                method_find_fn method_find = (method_find_fn)GetProcAddress(mono, "mono_class_get_method_from_name");
                invoke_fn invoke = (invoke_fn)GetProcAddress(mono, "mono_runtime_invoke");
                object_string_fn object_string = (object_string_fn)GetProcAddress(mono, "mono_object_to_string");
                string_utf8_fn string_utf8 = (string_utf8_fn)GetProcAddress(mono, "mono_string_to_utf8");
                mono_free_fn mono_free = (mono_free_fn)GetProcAddress(mono, "mono_free");
                if (!domain_get || !assembly_open || !assembly_image || !class_find || !method_find || !invoke || !object_string || !string_utf8 || !mono_free)
                {
                    installed = 1;
                    report_error("Required Mono embedding exports are unavailable in the original Player.");
                }
                else
                {
                    void* domain = domain_get();
                    if (domain)
                    {
                        WCHAR path[32768];
                        DWORD length = GetEnvironmentVariableW(L"UNIDOT_RUNTIME_BOOTSTRAP", path, 32768);
                        if (length > 0 && length < 32768)
                        {
                            int bytes = WideCharToMultiByte(CP_UTF8, 0, path, -1, NULL, 0, NULL, NULL);
                            char* utf8 = (char*)HeapAlloc(GetProcessHeap(), 0, bytes);
                            if (utf8)
                            {
                                WideCharToMultiByte(CP_UTF8, 0, path, -1, utf8, bytes, NULL, NULL);
                                void* assembly = assembly_open(domain, utf8);
                                HeapFree(GetProcessHeap(), 0, utf8);
                                if (!assembly) { installed = 1; report_error("Mono could not open the external Unidot bootstrap."); }
                                else
                                {
                                    void* type = class_find(assembly_image(assembly), "Unidot.Runtime", "Bootstrap");
                                    void* method = type ? method_find(type, "Install", 0) : NULL;
                                    if (!method) { installed = 1; report_error("Unidot.Runtime.Bootstrap.Install was not found."); }
                                    else
                                    {
                                        void* exception = NULL;
                                        void* result = invoke(method, NULL, NULL, &exception);
                                        if (exception)
                                        {
                                            void* formatting_error = NULL;
                                            void* value = object_string(exception, &formatting_error);
                                            char* text = value && !formatting_error ? string_utf8(value) : NULL;
                                            installed = 1;
                                            report_error(text ? text : "Bootstrap invocation failed.");
                                            if (text) mono_free(text);
                                        }
                                        else
                                        {
                                            char* text = result ? string_utf8(result) : NULL;
                                            if (text && lstrcmpA(text, "UNIDOT_NOT_READY") == 0) installed = 0;
                                            else installed = 1;
                                            if (text) mono_free(text);
                                        }
                                    }
                                }
                            }
                            else { installed = 1; report_error("Bootstrap path allocation failed."); }
                        }
                    }
                }
            }
        }
    }
    return CallNextHookEx(NULL, code, wparam, lparam);
}
