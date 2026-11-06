// The bindings cases that write bindings as Antril, compile Antril against them (System.Core, programs, binding
// profiles) or run larva bind / larva build on .ant files. Larva's own cases stay in Larva.Bindings.Tests.
using System.Diagnostics;
using Antril.Binding;
using Antril.Syntax;
using Larva.Bindings;
using Larva.Hir.Encoding;
using Larva.Hir.Linking;
using Larva.Hir.Lowering;
using Larva.IR;
using Larva.IR.Linking;
using Larva.Runtime.Vm;

namespace Antril.Tests;

/// <summary>Larva.Bindings with Antril: the bindings' Antril source, programs that call them on the three backends, binding profiles, and `larva bind` / `larva build` end to end.</summary>
public sealed unsafe class BindingsTests : CheckedTest
{
    /// <summary>System.Core compiled once with Antril: its .lva bytes (to link) and the assembly read back (to compile against).</summary>
    private static readonly Lazy<(AntrilResult Core, byte[] Bytes, global::Larva.Hir.HirAssembly? Reference)> Core = new(() =>
    {
        var core = AntrilCompiler.Compile(Repositories.LibrarySources("System.Core"), "System.Core");
        byte[] bytes = core.Success ? LvaFormat.Write(core.Assembly!) : [];
        return (core, bytes, core.Success ? LvaFormat.Read(bytes) : null);
    });

    static BindingsTests()
    {
        // In process, Antril is the language installed (as larva sets it from the packs).
        global::Larva.Build.QueenProject.SourceExtensions = [".ant"];
        BindingGenerator.Reserved = new HashSet<string>(Lexer.Keywords.Keys, StringComparer.Ordinal);
        BindingGenerator.SourceExtension = ".ant";
    }

    private static void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "the bindings tests use kernel32.dll");

    /// <summary>System.Core compiled; when it does not, the fact fails with the check the old runner made once.</summary>
    private (byte[] CoreBytes, global::Larva.Hir.HirAssembly CoreReference) RequireCore()
    {
        var (core, bytes, reference) = Core.Value;
        if (!core.Success)
        {
            Check("System.Core compiles", false, string.Join("; ", core.Diagnostics.Take(3)));
            Require();
        }
        return (bytes, reference!);
    }

    [Fact]
    public void Kernel32_bindings_written_as_Antril_compile_back_and_a_program_calls_them_everywhere()
    {
        RequireWindows();
        var exports = Exports.Read(Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
        var description = BindingDescription.Parse("""
            # A few of kernel32's functions.
            library kernel32.dll
            GetCurrentProcessId() : u32
            GetCurrentThreadId() : u32
            GetTickCount64() : u64
            Sleep(u32 milliseconds)
            GetModuleHandleW(u16* name) : void*
            Beep as MakeBeep(u32 frequency, u32 duration) : bool
            """);
        var assembly = BindingGenerator.Build(description, exports);

        string source = global::Antril.Pack.AntrilLanguage.Source(assembly);
        Log(source);
        Check("the Antril view writes [Import] declarations", source.Contains("[Import(\"kernel32.dll\")]") && source.Contains("GetCurrentProcessId() : u32;")
            && source.Contains("[Import(\"kernel32.dll\", \"Beep\")]") && source.Contains("MakeBeep(u32 frequency, u32 duration) : bool;"));

        var (coreBytes, coreReference) = RequireCore();
        var fromSource = AntrilCompiler.Compile([new SourceText(source, "Kernel32.ant")], [coreReference], "Kernel32FromSource");
        Check("…and that source compiles back as an Antril library", fromSource.Success, string.Join("; ", fromSource.Diagnostics.Take(4)));

        // A program against the generated assembly (as a reference, no source): the calls are direct C calls.
        const string program = """
            import System;
            import Bindings;

            Main() : i32
            {
                u32 pid = Kernel32.GetCurrentProcessId();
                u32 tid = Kernel32.GetCurrentThreadId();
                u64 ticks = Kernel32.GetTickCount64();
                Kernel32.Sleep(1);
                return (pid > 0 ? 1 : 0) + (tid > 0 ? 10 : 0) + (Kernel32.GetTickCount64() >= ticks ? 100 : 0);
            }
            """;
        byte[] bindingBytes = LvaFormat.Write(assembly);
        var compiled = AntrilCompiler.Compile([new SourceText(program, "main.ant")], [coreReference, LvaFormat.Read(bindingBytes)], "bapp");
        Check("a program compiles against the bindings assembly", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        if (!compiled.Success)
            return;
        var linked = HirLinker.Link("bapp", [coreBytes, bindingBytes, LvaFormat.Write(compiled.Assembly!)]);
        Check("and links with the core and the bindings", linked.Success, string.Join("; ", linked.Errors.Take(4)));
        if (!linked.Success)
            return;
        var lowered = HirLowerer.Lower(linked.Assembly!);
        Check("and lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(4)));
        if (!lowered.Success)
            return;
        var objectFile = Compiler.CompileObject(lowered.Module!);
        Check("and passes the IR checks", objectFile.Success, string.Join("; ", objectFile.TypeErrors.Concat<object>(objectFile.Diagnostics).Take(4)));
        if (!objectFile.Success)
            return;
        var ir = Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered.FunctionName(linked.Assembly!.EntryPoint!.Value) });
        Check("and links to a program", ir.Success, string.Join("; ", ir.Errors.Take(4)));
        if (!ir.Success)
            return;
        var (vm, llvm, x64, detail) = RunEverywhere(ir, ir.Entry!);
        Check("the program calls kernel32 through the bindings on the VM, LLVM and x64 → 111", vm == 111 && llvm == 111 && x64 == 111, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
    }

    // ── strings and func* ──

    /// <summary>A description with `str`, `String&lt;u16&gt;` and `func*` (ucrtbase and kernel32), used by a program on the three back ends.</summary>
    [Fact]
    public void Strings_and_func_pointers_in_Antril_run_everywhere()
    {
        RequireWindows();
        var (coreBytes, coreReference) = RequireCore();
        var crt = BindingDescription.Parse("""
            library ucrtbase.dll
            class Crt
            abs(i32 x) : i32
            strlen(str text) : u64
            qsort(void* items, u64 count, u64 size, func*<void*, void*> : i32 compare)
            """);
        var k32 = BindingDescription.Parse("""
            library kernel32.dll
            lstrlenW(String<u16> text) : i32
            GetModuleHandleA(str? name) : void*
            """);
        var crtAssembly = BindingGenerator.Build(crt, Exports.Read(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll")));
        var k32Assembly = BindingGenerator.Build(k32, Exports.Read(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
        string crtSource = global::Antril.Pack.AntrilLanguage.Source(crtAssembly, [coreReference]);
        Log(crtSource);
        Check("the Antril source writes str and func*", crtSource.Contains("strlen(str text) : u64;") && crtSource.Contains("func*<void*, void*> : i32 compare"));
        const string program = """
            import System;
            import System.Runtime.InteropServices;
            import Bindings;

            unsafe Compare(void* a, void* b) : i32
            {
                i32 x = *(i32*)a;
                i32 y = *(i32*)b;
                return x < y ? -1 : x > y ? 1 : 0;
            }

            Main() : i32
            {
                i32 r = Crt.abs(-7) == 7 ? 1 : 0;
                r += Crt.strlen("héllo") == 6 ? 10 : 0;
                r += Kernel32.lstrlenW(new String<u16>(Unicode.ToUtf16("año 2026"))) == 8 ? 100 : 0;
                unsafe
                {
                    i32* items = (i32*)Marshal.AllocHGlobal(4 * 4);
                    items[0] = 5; items[1] = 3; items[2] = 9; items[3] = 1;
                    Crt.qsort(items, 4, 4, Compare);
                    r += items[0] == 1 && items[1] == 3 && items[2] == 5 && items[3] == 9 ? 1000 : 0;
                    // `str?`: null crosses as NULL (the program's own module), a string as a C string.
                    r += Kernel32.GetModuleHandleA(null) != null && Kernel32.GetModuleHandleA("kernel32.dll") != null ? 10000 : 0;
                }
                return r;
            }
            """;
        byte[] crtBytes = LvaFormat.Write(crtAssembly), k32Bytes = LvaFormat.Write(k32Assembly);
        var compiled = AntrilCompiler.Compile([new SourceText(program, "main.ant")], [coreReference, LvaFormat.Read(crtBytes), LvaFormat.Read(k32Bytes)], "sapp");
        Check("a program compiles against bindings with strings and func*", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        if (!compiled.Success)
            return;
        var linked = HirLinker.Link("sapp", [coreBytes, crtBytes, k32Bytes, LvaFormat.Write(compiled.Assembly!)]);
        var lowered = linked.Success ? HirLowerer.Lower(linked.Assembly!) : null;
        var objectFile = lowered is { Success: true } ? Compiler.CompileObject(lowered.Module!) : null;
        var ir = objectFile is { Success: true } ? Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered!.FunctionName(linked.Assembly!.EntryPoint!.Value) }) : null;
        Check("…links, lowers and checks", ir is { Success: true },
            !linked.Success ? string.Join("; ", linked.Errors.Take(3)) : lowered is { Success: false } ? string.Join("; ", lowered.Errors.Take(3))
            : objectFile is { Success: false } ? string.Join("; ", objectFile.TypeErrors.Take(3)) : ir is { Success: false } ? string.Join("; ", ir.Errors.Take(3)) : "");
        if (ir is not { Success: true })
            return;
        var (vm, llvm, x64, detail) = RunEverywhere(ir, ir.Entry!);
        Check("abs, strlen(str), lstrlenW(String<u16>), qsort with an Antril comparison, GetModuleHandleA(str?) with null and a name → 11111",
            vm == 11111 && llvm == 11111 && x64 == 11111, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
    }

    // ── C headers ──

    [Fact]
    public void A_header_s_functions_and_constants_are_used_from_Antril()
    {
        RequireWindows();
        var (coreBytes, coreReference) = RequireCore();
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            /* A header in the subset the reader knows. */
            #pragma once
            #include <stddef.h>
            #define CRT_VERSION 3
            #define CRT_FLAG (0x10)
            #define CRT_MIN -2L
            #define CRT_TEXT "no"      // not an integer: ignored
            #define CRT_MACRO(x) (x)   // a function-like macro: ignored
            typedef unsigned int DWORD;
            typedef struct _CTX CTX;    // opaque
            typedef int (__cdecl *Comparer)(const void*, const void*);
            enum Mode { MODE_A, MODE_B = 5, MODE_C };
            typedef enum { LEVEL_LOW, LEVEL_HIGH } Level;
            __declspec(dllimport) DWORD __stdcall GetTickCount(void);
            int abs(int x);
            size_t strlen(const char* s);
            void qsort(void* base, size_t n, size_t size, Comparer cmp);
            int lstrlenW(const wchar_t* text);
            CTX* Open(const char* name, Level level);
            int Printf(const char* fmt, ...);
            double Sum(const double values[], int count);
            int Inline(int x) { return x; }
            """, "ucrtbase.dll", "Crt");
        var parsed = BindingDescription.Parse(description);
        var crt = BindingGenerator.Build(parsed, Exports.Read(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll")).Concat(["GetTickCount", "lstrlenW", "Open", "Sum", "Inline"]).ToList());
        string source = global::Antril.Pack.AntrilLanguage.Source(crt, [coreReference]);
        Log(source);
        Check("constants are static fields with their value in the Antril source", source.Contains("CRT_VERSION") && source.Contains("MODE_B"));
        const string program = """
            import System;
            import Bindings;

            Main() : i32
            {
                i32 r = Crt.abs(-3) == 3 ? 1 : 0;
                r += Crt.strlen("abcd") == 4 ? 10 : 0;
                r += Crt.CRT_VERSION == 3 && Crt.MODE_C == 6 && Crt.CRT_FLAG == 16 ? 100 : 0;
                return r;
            }
            """;
        byte[] crtBytes = LvaFormat.Write(crt);
        var compiled = AntrilCompiler.Compile([new SourceText(program, "main.ant")], [coreReference, LvaFormat.Read(crtBytes)], "happ");
        Check("a program uses the header's functions and constants", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        if (compiled.Success)
        {
            var linked = HirLinker.Link("happ", [coreBytes, crtBytes, LvaFormat.Write(compiled.Assembly!)]);
            var lowered = linked.Success ? HirLowerer.Lower(linked.Assembly!) : null;
            var objectFile = lowered is { Success: true } ? Compiler.CompileObject(lowered.Module!) : null;
            var ir = objectFile is { Success: true } ? Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered!.FunctionName(linked.Assembly!.EntryPoint!.Value) }) : null;
            if (ir is { Success: true })
            {
                var (vm, llvm, x64, detail) = RunEverywhere(ir, ir.Entry!);
                Check("…and runs on the VM, LLVM and x64 → 111", vm == 111 && llvm == 111 && x64 == 111, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
            }
            else
                Check("…links and lowers", false, !linked.Success ? string.Join("; ", linked.Errors.Take(3)) : lowered is { Success: false } ? string.Join("; ", lowered.Errors.Take(3))
                    : objectFile is { Success: false } ? string.Join("; ", objectFile.TypeErrors.Take(3)) : string.Join("; ", ir?.Errors.Take(3) ?? []));
        }
    }

    // ── C structs ──

    [Fact]
    public void C_structs_in_Antril_are_filled_by_kernel32_everywhere()
    {
        RequireWindows();
        var (coreBytes, coreReference) = RequireCore();
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            typedef unsigned short WORD;
            typedef unsigned long DWORD;
            #define NAME_LENGTH 4
            typedef struct _SYSTEMTIME { WORD wYear; WORD wMonth; WORD wDayOfWeek; WORD wDay; WORD wHour; WORD wMinute; WORD wSecond; WORD wMilliseconds; } SYSTEMTIME, *PSYSTEMTIME, *LPSYSTEMTIME;
            typedef struct _FILETIME { DWORD dwLowDateTime; DWORD dwHighDateTime; } FILETIME, *PFILETIME;
            struct Node;                                    /* forward: gets its body below */
            typedef struct Node Node;
            struct Node { Node* next; int value, weight; };
            typedef struct { FILETIME created, modified; unsigned char name[NAME_LENGTH]; double score; } Entry;
            typedef struct { int a; union { int b; float c; } u; } WithUnion;   /* opaque: a nested union */
            typedef struct { unsigned flags : 3; } Bits;                        /* opaque: bitfields */
            typedef struct { int big[1000]; } Big;                              /* opaque: too long an array */
            void GetSystemTime(LPSYSTEMTIME lpSystemTime);
            void GetSystemTimeAsFileTime(LPFILETIME lpSystemTimeAsFileTime);
            int FileTimeToSystemTime(const FILETIME* lpFileTime, LPSYSTEMTIME lpSystemTime);
            typedef FILETIME *LPFILETIME;
            Node* First(Node* list);
            Entry Make(void);
            int Walk(WithUnion* w, Bits* b);
            """, "kernel32.dll", "K32");
        var parsed = BindingDescription.Parse(description);
        var k32 = BindingGenerator.Build(parsed, ["GetSystemTime", "GetSystemTimeAsFileTime", "FileTimeToSystemTime", "First", "Walk"]);
        string source = global::Antril.Pack.AntrilLanguage.Source(k32, [coreReference]);
        Log(source);
        Check("the Antril source has the structs as value types with public fields, in C's order",
            source.Contains("value SYSTEMTIME") && source.Contains("u16 wYear;") && source.Contains("value Entry") && source.Contains("FILETIME created;") && source.Contains("Node* next;")
            && source.IndexOf("value FILETIME") < source.IndexOf("value Entry") && source.Contains("GetSystemTime(SYSTEMTIME* lpSystemTime);"));
        var compiledBack = AntrilCompiler.Compile([new SourceText(source, "K32.ant")], [coreReference], "K32Back");
        Check("…and compiles back as an Antril library", compiledBack.Success, string.Join("; ", compiledBack.Diagnostics.Take(4)));

        const string program = """
            import System;
            import System.Runtime.InteropServices;
            import Bindings;

            Main() : i32
            {
                i32 r = 0;
                unsafe
                {
                    SYSTEMTIME now;
                    K32.GetSystemTime(&now);
                    r += now.wYear >= 2026 && now.wMonth >= 1 && now.wMonth <= 12 && now.wDay >= 1 && now.wDay <= 31 ? 1 : 0;
                    FILETIME ft;
                    K32.GetSystemTimeAsFileTime(&ft);
                    SYSTEMTIME back;
                    r += K32.FileTimeToSystemTime(&ft, &back) != 0 && back.wYear == now.wYear ? 10 : 0;
                    r += sizeof(SYSTEMTIME) == 16 && sizeof(FILETIME) == 8 && sizeof(Entry) == 32 && sizeof(Node) == 16 ? 100 : 0;
                    Entry e;
                    e.modified.dwHighDateTime = 7;
                    u32* raw = (u32*)&e;
                    r += raw[3] == 7 ? 1000 : 0;
                }
                return r;
            }
            """;
        byte[] k32Bytes = LvaFormat.Write(k32);
        var compiled = AntrilCompiler.Compile([new SourceText(program, "main.ant")], [coreReference, LvaFormat.Read(k32Bytes)], "sapp");
        Check("a program declares the structs as locals and passes their addresses to kernel32", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        if (compiled.Success)
        {
            var linked = HirLinker.Link("sapp", [coreBytes, k32Bytes, LvaFormat.Write(compiled.Assembly!)]);
            var lowered = linked.Success ? HirLowerer.Lower(linked.Assembly!) : null;
            var objectFile = lowered is { Success: true } ? Compiler.CompileObject(lowered.Module!) : null;
            var ir = objectFile is { Success: true } ? Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered!.FunctionName(linked.Assembly!.EntryPoint!.Value) }) : null;
            if (ir is { Success: true })
            {
                var (vm, llvm, x64, detail) = RunEverywhere(ir, ir.Entry!);
                Check("…and kernel32 fills them with C's layout on the VM, LLVM and x64 → 1111", vm == 1111 && llvm == 1111 && x64 == 1111, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
            }
            else
                Check("…links and lowers", false, !linked.Success ? string.Join("; ", linked.Errors.Take(3)) : lowered is { Success: false } ? string.Join("; ", lowered.Errors.Take(3))
                    : objectFile is { Success: false } ? string.Join("; ", objectFile.TypeErrors.Take(3)) : string.Join("; ", ir?.Errors.Take(3) ?? []));
        }
    }

    /// <summary>A real header: windows.h (lean) through clang, all of it as one assembly written as Antril.</summary>
    [Fact]
    public void All_of_windows_h_written_as_Antril_compiles_back()
    {
        RequireWindows();
        Assert.SkipWhen(HeaderReader.FindClang() is null, "no clang: windows.h is not read");
        var (_, coreReference) = RequireCore();
        string folder = TestEnvironment.TempFolder("headers");
        string windows = Path.Combine(folder, "windows_lean.h");
        File.WriteAllText(windows, "#define WIN32_LEAN_AND_MEAN\n#include <windows.h>\n");
        var real = new HeaderReader(longIs64: false);
        string realDescription = real.ReadFile(windows, "kernel32.dll", "Win32");
        var realParsed = BindingDescription.Parse(realDescription);
        var watch = Stopwatch.StartNew();
        var win32 = BindingGenerator.Build(realParsed);
        string win32Source = global::Antril.Pack.AntrilLanguage.Source(win32, [coreReference]);
        var win32Back = AntrilCompiler.Compile([new SourceText(win32Source, "Win32.ant")], [coreReference], "Win32Back");
        watch.Stop();
        Check("all of windows.h as one assembly writes as Antril and compiles back", win32Back.Success,
            $"{win32Source.Length / 1024} KB of Antril, {watch.ElapsedMilliseconds} ms; {string.Join("; ", win32Back.Diagnostics.Take(5))}");
    }

    // ── C unions ──

    /// <summary>C unions as `[ValueLayout(Layout.Explicit)]` values with every member at offset 0, and `#pragma pack`; LARGE_INTEGER filled by kernel32 on the three backends.</summary>
    [Fact]
    public void C_unions_in_Antril_are_filled_by_kernel32_everywhere()
    {
        RequireWindows();
        var (coreBytes, coreReference) = RequireCore();
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            typedef unsigned long DWORD;
            typedef long LONG;
            typedef long long LONGLONG;
            typedef union _LARGE_INTEGER { struct { DWORD LowPart; LONG HighPart; }; struct { DWORD LowPart; LONG HighPart; } u; LONGLONG QuadPart; } LARGE_INTEGER, *PLARGE_INTEGER;
            int QueryPerformanceCounter(LARGE_INTEGER* lpPerformanceCount);
            typedef union { int i; double d; unsigned char b[3]; } Value;
            typedef struct { Value v; short tag; } Tagged;
            union Small { char c; short s; };
            struct Pair { union Small a, b; };
            #pragma pack(push, 1)
            typedef struct { unsigned char tag; unsigned int value; unsigned short code; } Packed;
            #pragma pack(push, 2)
            typedef struct { char c; double d; } Packed2;
            #pragma pack(pop)
            #pragma pack(pop)
            typedef struct { char c; double d; } Natural;
            """, "kernel32.dll", "K32");
        var parsed = BindingDescription.Parse(description);
        var k32 = BindingGenerator.Build(parsed, ["QueryPerformanceCounter"]);
        string source = global::Antril.Pack.AntrilLanguage.Source(k32, [coreReference]);
        Log(source);
        Check("a union is `[ValueLayout(Layout.Explicit)]` with each member at `[FieldOffset(0)]`; a packed struct `[ValueLayout(Layout.Sequential, Pack = 1)]`; an array `fixed u8[3]`",
            source.Contains("[ValueLayout(Layout.Explicit)]") && source.Contains("[FieldOffset(0)] i64 QuadPart;") && source.Contains("[FieldOffset(0)] LARGE_INTEGER_u u;")
            && source.Contains("[ValueLayout(Layout.Sequential, Pack = 1)]") && source.Contains("[FieldOffset(0)] fixed u8[3] b;") && source.Contains("import System.Runtime.InteropServices;"));
        var back = AntrilCompiler.Compile([new SourceText(source, "K32.ant")], [coreReference], "K32Back");
        var backUnion = back.Assembly?.Types.FirstOrDefault(t => t.Name == "LARGE_INTEGER");
        Check("the Antril source compiles back with the union's members and their offsets",
            back.Success && backUnion is { Fields.Count: 3, Properties.Count: 0 } && backUnion.Fields.All(f => f.Attributes.Length == 1) && backUnion.Attributes.Count == 1,
            back.Success ? $"{backUnion?.Fields.Count} fields, {backUnion?.Properties.Count} properties" : string.Join("; ", back.Diagnostics.Take(3)));
        const string program = """
            import System;
            import Bindings;

            Main() : i32
            {
                i32 r = 0;
                unsafe
                {
                    LARGE_INTEGER counter;
                    r += K32.QueryPerformanceCounter(&counter) != 0 && counter.QuadPart > 0 && *(i64*)&counter == counter.QuadPart
                        && counter.u.HighPart == counter.anon0.HighPart && (i64)counter.u.LowPart == (counter.QuadPart & 0xFFFFFFFF) ? 1 : 0;
                    r += sizeof(LARGE_INTEGER) == 8 && sizeof(Value) == 8 && sizeof(Tagged) == 16 && sizeof(Small) == 2 && sizeof(Pair) == 4
                        && sizeof(Packed) == 7 && sizeof(Packed2) == 10 && sizeof(Natural) == 16 ? 10 : 0;
                    Tagged t;
                    t.v.d = 2.5;
                    t.tag = 3;
                    r += t.v.d == 2.5 && *(f64*)&t.v == 2.5 && ((i16*)&t)[4] == 3 ? 100 : 0;
                    Value v;
                    v.i = -1;
                    Packed p;
                    p.value = 0x01020304;
                    r += v.b[0] == 255 && v.b[2] == 255 && ((u8*)&v)[1] == 255 && ((u8*)&p)[1] == 4 && ((u8*)&p)[4] == 1 ? 1000 : 0;
                }
                return r;
            }
            """;
        byte[] k32Bytes = LvaFormat.Write(k32);
        var compiled = AntrilCompiler.Compile([new SourceText(program, "main.ant")], [coreReference, LvaFormat.Read(k32Bytes)], "uapp");
        Check("a program reads and writes the unions' members and the packed struct", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        if (compiled.Success)
        {
            var linked = HirLinker.Link("uapp", [coreBytes, k32Bytes, LvaFormat.Write(compiled.Assembly!)]);
            var lowered = linked.Success ? HirLowerer.Lower(linked.Assembly!) : null;
            var objectFile = lowered is { Success: true } ? Compiler.CompileObject(lowered.Module!) : null;
            var ir = objectFile is { Success: true } ? Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered!.FunctionName(linked.Assembly!.EntryPoint!.Value) }) : null;
            if (ir is { Success: true })
            {
                var (vm, llvm, x64, detail) = RunEverywhere(ir, ir.Entry!);
                Check("…and kernel32 fills LARGE_INTEGER on the VM, LLVM and x64 → 1111", vm == 1111 && llvm == 1111 && x64 == 1111, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
            }
            else
                Check("…links and lowers", false, !linked.Success ? string.Join("; ", linked.Errors.Take(3)) : lowered is { Success: false } ? string.Join("; ", lowered.Errors.Take(3))
                    : objectFile is { Success: false } ? string.Join("; ", objectFile.TypeErrors.Take(3)) : string.Join("; ", ir?.Errors.Take(3) ?? []));
        }
    }

    // ── SDL3 ──

    /// <summary>
    /// SDL3 from its headers, written as Antril: the source compiles back, and a program pushes an event through SDL's
    /// queue (the SDL_Event union), paints a surface and reads its pixels on the VM, LLVM and x64. Skipped without SDL:
    /// LARVA_SDL names a folder with SDL3.dll and include/SDL3 (or with build/SDL3.dll and SDL-main/include, as a CMake
    /// build of the source leaves them).
    /// </summary>
    [Fact]
    public void SDL3_bindings_in_Antril_run_everywhere()
    {
        var (dll, include) = RequireSdl();
        var (coreBytes, coreReference) = RequireCore();
        var reader = new HeaderReader(longIs64: false);
        string text = reader.ReadFile(Path.Combine(include, "SDL3", "SDL.h"), "SDL3.dll", "SDL", includeDirectories: [include]);
        var description = BindingDescription.Parse(text);
        description.KeepExported(Exports.Read(dll));
        var sdl = BindingGenerator.Build(description, Exports.Read(dll));
        string source = global::Antril.Pack.AntrilLanguage.Source(sdl, [coreReference]);
        var back = AntrilCompiler.Compile([new SourceText(source, "SDL3.ant")], [coreReference], "SDL3Back");
        Check("the SDL3 bindings written as Antril compile back", back.Success, $"{source.Length / 1024} KB; {string.Join("; ", back.Diagnostics.Take(4))}");
        const string program = """
            import System;
            import Bindings;

            Main() : i32
            {
                i32 r = 0;
                unsafe
                {
                    r += SDL.SDL_Init((u32)SDL.SDL_INIT_EVENTS) && SDL.SDL_GetVersion() / 1000000 == SDL.SDL_MAJOR_VERSION ? 1 : 0;

                    // A user event through SDL's queue: the union written, pushed, polled and read back.
                    u32 kind = SDL.SDL_RegisterEvents(1);
                    SDL_Event sent;
                    sent.user.type = kind;
                    sent.user.code = 42;
                    sent.user.data1 = (void*)1234;
                    bool pushed = SDL.SDL_PushEvent(&sent);
                    SDL_Event got;
                    bool found = false;
                    loop (!found && SDL.SDL_PollEvent(&got))
                        found = got.type == kind;
                    r += pushed && found && got.user.code == 42 && (u64)got.user.data1 == 1234 && got.common.timestamp > 0 ? 10 : 0;
                    r += sizeof(SDL_Event) == 128 && sizeof(SDL_UserEvent) == 40 && sizeof(SDL_Surface) == 48 && sizeof(SDL_Rect) == 16 ? 100 : 0;

                    // A surface: SDL fills its struct, a rectangle painted and its pixels read back.
                    SDL_Surface* surface = SDL.SDL_CreateSurface(4, 3, SDL.SDL_PIXELFORMAT_RGBA32);
                    SDL_Rect rect;
                    rect.x = 1;
                    rect.y = 1;
                    rect.w = 2;
                    rect.h = 2;
                    bool filled = SDL.SDL_FillSurfaceRect(surface, &rect, 0xFF00FF00u32);
                    u32* pixels = (u32*)surface->pixels;
                    i32 stride = surface->pitch / 4;
                    r += surface->w == 4 && surface->h == 3 && surface->pitch >= 16 && filled
                        && pixels[1 * stride + 1] == 0xFF00FF00u32 && pixels[2 * stride + 2] == 0xFF00FF00u32 && pixels[0] == 0 ? 1000 : 0;
                    SDL_Rect other;
                    other.x = 2;
                    other.y = 0;
                    other.w = 5;
                    other.h = 2;
                    SDL_Rect both;
                    r += SDL.SDL_GetRectIntersection(&rect, &other, &both) && both.x == 2 && both.y == 1 && both.w == 1 && both.h == 1 ? 10000 : 0;
                    SDL.SDL_DestroySurface(surface);
                    SDL.SDL_Quit();
                }
                return r;
            }
            """;
        byte[] sdlBytes = LvaFormat.Write(sdl);
        var compiled = AntrilCompiler.Compile([new SourceText(program, "main.ant")], [coreReference, LvaFormat.Read(sdlBytes)], "sdlapp");
        Check("a program uses SDL_Init, the SDL_Event union, SDL_Surface and SDL_Rect", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        if (!compiled.Success)
            return;
        // SDL3.dll where the in-process runtimes find it (next to the test runner).
        string local = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        if (!File.Exists(local) || new FileInfo(local).Length != new FileInfo(dll).Length)
            File.Copy(dll, local, overwrite: true);
        var linked = HirLinker.Link("sdlapp", [coreBytes, sdlBytes, LvaFormat.Write(compiled.Assembly!)]);
        var lowered = linked.Success ? HirLowerer.Lower(linked.Assembly!) : null;
        var objectFile = lowered is { Success: true } ? Compiler.CompileObject(lowered.Module!) : null;
        var ir = objectFile is { Success: true } ? Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered!.FunctionName(linked.Assembly!.EntryPoint!.Value) }) : null;
        if (ir is { Success: true })
        {
            var (vm, llvm, x64, detail) = RunEverywhere(ir, ir.Entry!);
            Check("…and runs with SDL3.dll on the VM, LLVM and x64 → 11111", vm == 11111 && llvm == 11111 && x64 == 11111, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
        else
            Check("…links and lowers", false, !linked.Success ? string.Join("; ", linked.Errors.Take(3)) : lowered is { Success: false } ? string.Join("; ", lowered.Errors.Take(3))
                : objectFile is { Success: false } ? string.Join("; ", objectFile.TypeErrors.Take(3)) : string.Join("; ", ir?.Errors.Take(3) ?? []));
    }

    /// <summary>SDL3.dll and its include folder (LARVA_SDL), and clang; skipped without them.</summary>
    private static (string Dll, string Include) RequireSdl()
    {
        string root = Environment.GetEnvironmentVariable("LARVA_SDL") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SDL-main");
        string? dll = new[] { Path.Combine(root, "SDL3.dll"), Path.Combine(root, "build", "SDL3.dll") }.FirstOrDefault(File.Exists);
        string? include = new[] { Path.Combine(root, "include"), Path.Combine(root, "SDL-main", "include") }.FirstOrDefault(d => File.Exists(Path.Combine(d, "SDL3", "SDL.h")));
        Assert.SkipWhen(dll is null || include is null || HeaderReader.FindClang() is null, "SDL3 not found (LARVA_SDL: a folder with SDL3.dll and include/SDL3) or no clang");
        return (dll!, include!);
    }

    // ── binding profiles ──
    // Classes that implement System.Bindings.IBindingProfile, run by `larva bind --profile`.

    /// <summary>A new folder with the two headers of the `Lib` profiles: inc/lib_core.h and inc/lib_audio.h.</summary>
    private static string ProfileFolder()
    {
        string folder = TestEnvironment.TempFolder("profiles");
        Directory.CreateDirectory(Path.Combine(folder, "inc"));
        File.WriteAllText(Path.Combine(folder, "inc", "lib_core.h"), """
            typedef unsigned int LIB_Flags;
            #define LIB_FLAG_A 0x1u
            #define LIB_FLAG_B 0x2u
            #define LIB_FLAG_C 0x4u
            #define LIB_FLAG_NEGATIVE -1
            #define LIB_VERSION 3
            typedef enum LIB_Mode { LIB_MODE_FAST, LIB_MODE_SAFE = 4, LIB_MODE_3D = 8 } LIB_Mode;
            typedef struct LIB_Point { int x_pos; int y_pos; LIB_Mode mode; } LIB_Point;
            int LIB_Open(LIB_Flags flags, LIB_Mode mode);
            LIB_Flags LIB_GetFlags(void);
            void LIB_Move(LIB_Point* point, int dx);
            int LIB_move(int x);
            """);
        File.WriteAllText(Path.Combine(folder, "inc", "lib_audio.h"), """
            typedef struct LIB_AudioSpec { int freq; } LIB_AudioSpec;
            int LIB_PlayAudio(LIB_AudioSpec* spec);
            #define LIB_AUDIO_MAX 8
            """);
        return folder;
    }

    /// <summary>
    /// Two headers shaped by settings and hooks (enums from C and from constants, types from typedefs, prefixes, case, a
    /// rename, a clash, namespaces and classes by header, files by namespace); the source compiled with code written against it.
    /// </summary>
    [Fact]
    public void A_profile_shapes_two_headers_and_code_compiles_against_the_shaped_names()
    {
        RequireWindows();
        var (_, coreReference) = RequireCore();
        string folder = ProfileFolder();
        File.WriteAllText(Path.Combine(folder, "LibProfile.ant"), """
            import System;
            import System.Bindings;

            into namespace Profiles
            {
                public class LibProfile : BindingProfile
                {
                    public:
                        override Configure(BindingSettings s)
                        {
                            s.Libraries.Add("lib.dll");
                            s.Headers.Add("inc/lib_core.h");
                            s.Headers.Add("inc/lib_audio.h");
                            s.Includes.Add("inc");
                            s.Namespace = "Lib";
                            s.Class = "Lib";
                            s.Strip.Add("LIB_");
                            s.TypeCase = NameCase.Pascal;
                            s.FunctionCase = NameCase.Pascal;
                            s.ConstantCase = NameCase.Pascal;
                            s.FieldCase = NameCase.Pascal;
                            s.ParameterCase = NameCase.Camel;
                            s.NamespaceByHeader = true;
                            s.HeaderStrip.Add("lib_");
                            s.Split = SplitBy.Namespace;
                            s.Source = "out";
                        }

                        // A bitflag enum from constants, taking what C wrote with LIB_Flags.
                        override Start(BindingModel m)
                        {
                            m.MakeEnum("Flags", "LIB_FLAG_?", true).Typedef("LIB_Flags");
                        }

                        // Each header's functions and constants in a class of its own: `CoreApi`, `AudioApi`.
                        override Function(CFunction f) : bool
                        {
                            f.Class = Names.Pascal(Names.Strip(Names.Stem(f.Header), "lib_")) + "Api";
                            return true;
                        }

                        override Constant(CConstant c) : bool
                        {
                            c.Class = Names.Pascal(Names.Strip(Names.Stem(c.Header), "lib_")) + "Api";
                            return true;
                        }

                        override Name(NameKind kind, str cName, str proposed) : str =>
                            cName == "LIB_GetFlags" ? "CurrentFlags" : proposed;
                }
            }
            """);
        var (dryCode, dryText) = Larva("bind", "--profile", Path.Combine(folder, "LibProfile.ant"), "--dry-run");
        Log(dryText);
        Check("--dry-run prints the description the profile leaves, writing nothing", dryCode == 0 && dryText.Contains("nothing written") && !File.Exists(Path.Combine(folder, "LibBindings.dll")), dryText.Trim());
        Check("an enum from constants (MakeEnum) takes what C wrote with its typedef; its members lose their shared prefix",
            dryText.Contains("enum Flags : i32 flags { A = 1, B = 2, C = 4 }") && dryText.Contains("LIB_Open as Open(Flags flags, Mode mode) : i32") && dryText.Contains("const FlagNegative = -1"));
        Check("an enum from C: `Mode`, its members `Fast`, `Safe`, and `_3d` (a name cannot start with a digit)", dryText.Contains("enum Mode : i32 { Fast = 0, Safe = 4, _3d = 8 }"));
        Check("prefixes stripped and case set by the settings, a rename by `Name`, fields and parameters too",
            dryText.Contains("LIB_GetFlags as CurrentFlags() : Flags") && dryText.Contains("struct Point { i32 XPos; i32 YPos; Mode Mode }") && dryText.Contains("LIB_Move as Move(Point* point, i32 dx)"));
        Check("two names in one class: the later keeps its C name, and larva says so", dryText.Contains("LIB_move(i32 x) : i32") && dryText.Contains("warning: the function `LIB_move`"));
        Check("namespaces by header, classes set by a hook (`CoreApi`, `AudioApi`), files by namespace",
            dryText.Contains("file Lib.Core\nnamespace Lib.Core") && dryText.Contains("namespace Lib.Audio\nclass AudioApi") && dryText.Contains("const AudioMax = 8"));

        var (code, text) = Larva("bind", "--profile", Path.Combine(folder, "LibProfile.ant"));
        string out1 = Path.Combine(folder, "out");
        Check("larva bind --profile writes the library (LibBindings.dll) and the source the settings name, split by namespace",
            code == 0 && global::Larva.Build.QueenProject.IsAssembly(Path.Combine(folder, "LibBindings.dll"))
            && File.Exists(Path.Combine(out1, "Lib.Core.ant")) && File.Exists(Path.Combine(out1, "Lib.Audio.ant")), text.Trim());
        if (code == 0)
        {
            const string user = """
                import System;
                import Lib.Core;
                import Lib.Audio;

                F() : i32
                {
                    Flags f = Flags.A | Flags.B;
                    i32 r = CoreApi.Open(f, Mode.Safe);
                    Point p;
                    p.XPos = 1;
                    p.Mode = Mode._3d;
                    unsafe { CoreApi.Move(&p, 2); }
                    AudioSpec spec;
                    spec.Freq = AudioApi.AudioMax;
                    return r + (i32)CoreApi.CurrentFlags() + CoreApi.FlagNegative + CoreApi.Version;
                }
                """;
            var sources = Directory.GetFiles(out1).Select(f => new SourceText(File.ReadAllText(f), Path.GetFileName(f))).ToList();
            var compiled = AntrilCompiler.Compile([.. sources, new SourceText(user, "user.ant")], [coreReference], "LibUser");
            Check("the files compile together, and code written against the shaped names compiles with them", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        }
    }

    /// <summary>A profile's own mistakes: no profile class, one that throws.</summary>
    [Fact]
    public void A_profile_s_mistakes_are_told()
    {
        RequireWindows();
        string folder = ProfileFolder();
        File.WriteAllText(Path.Combine(folder, "None.ant"), "into namespace Profiles { public class Nothing { } }\n");
        var (noneCode, noneText) = Larva("bind", "--profile", Path.Combine(folder, "None.ant"));
        Check("a file with no IBindingProfile class is told so", noneCode == 1 && noneText.Contains("no class that is an IBindingProfile"), noneText.Trim());
        File.WriteAllText(Path.Combine(folder, "Throws.ant"), """
            import System;
            import System.Bindings;
            into namespace Profiles
            {
                public class Throws : BindingProfile
                {
                    public:
                        override Configure(BindingSettings s) { s.Libraries.Add("kernel32.dll"); s.Headers.Add("inc/lib_core.h"); }
                        override Start(BindingModel m) { throw new InvalidOperationError("not today"); }
                }
            }
            """);
        var (throwsCode, throwsText) = Larva("bind", "--profile", Path.Combine(folder, "Throws.ant"));
        Check("a profile that throws stops the binding, and says why", throwsCode == 1 && throwsText.Contains("the profile failed"), throwsText.Trim());
    }

    /// <summary>
    /// A library of the system's (kernel32) written with its source; a project that keeps its profile with it; DEPS `lib:`
    /// and FILES `copy:`; the binding as a Hive package (a folder registry and the project's own cache, both temporary)
    /// installed by another project.
    /// </summary>
    [Fact]
    public void A_profile_binds_kernel32_and_the_library_is_built_copied_and_packaged()
    {
        RequireWindows();
        string folder = ProfileFolder();
        // A library of the system's, the header and library on the command line, kernel32's functions checked.
        File.WriteAllText(Path.Combine(folder, "k32.h"), "typedef unsigned long DWORD;\n#define K32_TAG_ONE 1\nDWORD GetCurrentProcessId(void);\nDWORD GetTickCount(void);\n");
        File.WriteAllText(Path.Combine(folder, "K32.ant"), """
            import System;
            import System.Bindings;
            into namespace Profiles
            {
                public class K32 : BindingProfile
                {
                    public:
                        override Configure(BindingSettings s)
                        {
                            s.Namespace = "Win";
                            s.Class = "Kernel";
                            s.Strip.Add("K32_");
                            s.Strip.Add("Get");
                            s.ConstantCase = NameCase.Pascal;
                        }
                }
            }
            """);
        var (k32Code, k32Text) = Larva("bind", "--profile", Path.Combine(folder, "K32.ant"), "kernel32.dll", Path.Combine(folder, "k32.h"), "--source", Path.Combine(folder, "k32"));
        string k32Source = Directory.Exists(Path.Combine(folder, "k32")) ? string.Concat(Directory.GetFiles(Path.Combine(folder, "k32")).Select(File.ReadAllText)) : "";
        Check("larva bind --profile K32.ant kernel32.dll k32.h --source: the library and the source, the settings' names",
            k32Code == 0 && File.Exists(Path.Combine(folder, "KernelBindings.dll")) && k32Source.Contains("CurrentProcessId() : u32") && k32Source.Contains("TagOne = 1"), k32Text.Trim() + k32Source);

        // A project that keeps its profile with it: the profile is one of its sources, built with System.Bindings.
        string kept = Path.Combine(folder, "kept");
        Directory.CreateDirectory(Path.Combine(kept, "bindings"));
        File.Copy(Path.Combine(folder, "K32.ant"), Path.Combine(kept, "K32.ant"));
        foreach (string file in Directory.Exists(Path.Combine(folder, "k32")) ? Directory.GetFiles(Path.Combine(folder, "k32")) : [])
            File.Copy(file, Path.Combine(kept, "bindings", Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(kept, "Kept.queen"), "queen 1.0;\nchunk PROJ:\n   name: Kept\n");
        File.WriteAllText(Path.Combine(kept, "main.ant"), "import System;\nimport Win;\nMain() { Console.WriteLine(Kernel.CurrentProcessId() > 0u32); }\n");
        var (keptCode, keptText) = Larva("build", "--check", Path.Combine(kept, "Kept.queen"));
        Check("a project with its binding profile among its sources builds (System.Bindings comes with it)", keptCode == 0, keptText.Trim());

        // DEPS `lib:` and FILES `copy:` land next to the build.
        Directory.CreateDirectory(Path.Combine(kept, "native"));
        File.WriteAllText(Path.Combine(kept, "native", "Fake.dll"), "not a real library");
        File.WriteAllText(Path.Combine(kept, "data.txt"), "data");
        File.WriteAllText(Path.Combine(kept, "Kept.queen"), "queen 1.0;\nchunk PROJ:\n   name: Kept\nchunk BUILD:\n   backend: x64\nchunk DEPS:\n   lib: native/Fake.dll\nchunk FILES:\n   copy: data.txt,\n   into: assets\n");
        var (copyCode, copyText) = Larva("build", Path.Combine(kept, "Kept.queen"));
        Check("larva build copies the project's native libraries (`lib:`) and FILES next to the program",
            copyCode == 0 && File.Exists(Path.Combine(kept, "bin", "Fake.dll")) && File.Exists(Path.Combine(kept, "bin", "assets", "data.txt")), copyText.Trim());

        // A binding as a Hive package: the compiled library (`assembly:`) and its native library (`lib:`), signed,
        // published to a folder registry, installed by another project and built against.
        string package = Path.Combine(folder, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "Win.queen"), "queen 1.0;\nchunk PROJ:\n   name: Win,\n   type: library,\n   version: v1.2.0,\n   author: Tests\nchunk SOURCES:\n   scan: false\nchunk DEPS:\n   assembly: ../KernelBindings.dll,\n   lib: ../kept/native/Fake.dll\nchunk HIVE:\n   package: Tests/Win,\n   sign: test.key,\n   provider: file:../registry\n");
        var (keyMade, keyMadeText) = Larva("hive", "key", "-o", Path.Combine(package, "test.key"));
        var (published, publishText) = Larva("hive", "publish", Path.Combine(package, "Win.queen"));
        Check("larva hive publish packs a compiled library (its `assembly:` and `lib:`), signed, into a folder registry",
            keyMade == 0 && published == 0 && File.Exists(Path.Combine(folder, "registry", "Tests", "Win", "index.json")), keyMadeText + publishText);
        string consumer = Path.Combine(folder, "user");
        Directory.CreateDirectory(consumer);
        File.WriteAllText(Path.Combine(consumer, "User.queen"), $"queen 1.0;\nchunk PROJ:\n   name: User\nchunk BUILD:\n   backend: x64\nchunk HIVE:\n   provider: \"file:{Path.Combine(folder, "registry").Replace('\\', '/')}\",\n   cache: .hive\n");
        File.WriteAllText(Path.Combine(consumer, "main.ant"), "import System;\nimport Win;\nMain() { Console.WriteLine(Kernel.CurrentProcessId() > 0u32); }\n");
        var (added, addText) = Larva("hive", "add", "Tests/Win@^1.2", Path.Combine(consumer, "User.queen"));
        var (usedCode, usedText) = Larva("build", Path.Combine(consumer, "User.queen"));
        Check("…another project adds it (larva hive add), and larva build compiles against it and copies its native library",
            added == 0 && usedCode == 0 && File.Exists(Path.Combine(consumer, "bin", "Fake.dll")), addText + usedText);
    }

    /// <summary>SDL3's profile end to end (skipped without SDL).</summary>
    [Fact]
    public void SDL3_s_profile_shapes_its_headers_and_a_program_runs_against_them()
    {
        var (dll, include) = RequireSdl();
        var (coreBytes, coreReference) = RequireCore();
        string sdlFolder = TestEnvironment.TempFolder("sdl-profile");
        string Path2(string path) => path.Replace('\\', '/');
        File.WriteAllText(Path.Combine(sdlFolder, "SdlProfile.ant"), $$"""
            import System;
            import System.Bindings;

            into namespace Profiles
            {
                public class SdlProfile : BindingProfile
                {
                    public:
                        override Configure(BindingSettings s)
                        {
                            s.Libraries.Add("{{Path2(dll)}}");
                            s.Headers.Add("{{Path2(Path.Combine(include, "SDL3", "SDL.h"))}}");
                            s.Includes.Add("{{Path2(include)}}");
                            s.Namespace = "SDL3";
                            s.Class = "SDL";
                            s.Strip.Add("SDL_");
                            s.TypeCase = NameCase.Pascal;
                            s.FunctionCase = NameCase.Pascal;
                            s.ConstantCase = NameCase.Pascal;
                            s.FieldCase = NameCase.Pascal;
                            s.ParameterCase = NameCase.Camel;
                            s.NamespaceByHeader = true;
                            s.ClassByHeader = true;
                            s.Split = SplitBy.Header;
                            s.Source = "generated";
                        }

                        override Start(BindingModel m)
                        {
                            m.MakeEnum("InitFlags", "SDL_INIT_*", true).Typedef("SDL_InitFlags");
                            m.MakeEnum("WindowFlags", "SDL_WINDOW_*", "SDL_WINDOW_SURFACE_*", true).Typedef("SDL_WindowFlags");
                        }

                        override Name(NameKind kind, str cName, str proposed) : str => cName == "SDL_Init" ? "Initialize" : proposed;
                }
            }
            """);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (sdlCode, sdlText) = Larva("bind", "--profile", Path.Combine(sdlFolder, "SdlProfile.ant"), "-o", Path.Combine(sdlFolder, "SDL3.Bindings.dll"));
        long shapedMs = watch.ElapsedMilliseconds;
        string generated = Path.Combine(sdlFolder, "generated");
        var sdlSources = Directory.Exists(generated) ? Directory.GetFiles(generated).ToDictionary(f => Path.GetFileName(f), File.ReadAllText) : [];
        string all = string.Concat(sdlSources.Values);
        Check("SDL3's profile: InitFlags/WindowFlags as typed enums, a namespace, class and file per header, no system header's types",
            sdlCode == 0 && sdlSources.Count > 40 && !sdlSources.Keys.Any(k => k.StartsWith("Corecrt", StringComparison.OrdinalIgnoreCase))
            && all.Contains("bitflag enum InitFlags") && all.Contains("Initialize(InitFlags flags) : bool") && all.Contains(") : WindowFlags;"),
            $"{sdlSources.Count} files, {shapedMs} ms: {sdlText.Trim()[..Math.Min(400, sdlText.Trim().Length)]}");
        if (sdlCode != 0)
            return;
        const string sdlProgram = """
            import System;
            import SDL3.Init;
            import SDL3.Events;
            import SDL3.Surface;
            import SDL3.Rect;
            import SDL3.Pixels;
            import SDL3.Version;

            Main() : i32
            {
                i32 r = 0;
                unsafe
                {
                    r += Init.Initialize(InitFlags.Events) && Init.WasInit(InitFlags.Events).HasFlag(InitFlags.Events)
                        && Version.GetVersion() / 1000000 == Version.MajorVersion ? 1 : 0;

                    // A user event through SDL's queue: the union written, pushed, polled and read back.
                    u32 kind = Events.RegisterEvents(1);
                    Event sent;
                    sent.User.Type = kind;
                    sent.User.Code = 42;
                    bool pushed = Events.PushEvent(&sent);
                    Event got;
                    bool found = false;
                    loop (!found && Events.PollEvent(&got))
                        found = got.Type == kind;
                    r += pushed && found && got.User.Code == 42 && kind >= (u32)EventType.User ? 10 : 0;
                    r += sizeof(Event) == 128 && sizeof(UserEvent) == 40 && sizeof(SDL3.Surface.Surface) == 48 && sizeof(SDL3.Rect.Rect) == 16 ? 100 : 0;

                    // A surface: SDL fills its struct, a rectangle painted and its pixels read back.
                    SDL3.Surface.Surface* surface = SurfaceFunctions.CreateSurface(4, 3, PixelFormat.Rgba32);
                    SDL3.Rect.Rect rect;
                    rect.X = 1;
                    rect.Y = 1;
                    rect.W = 2;
                    rect.H = 2;
                    bool filled = SurfaceFunctions.FillSurfaceRect(surface, &rect, 0xFF00FF00u32);
                    u32* pixels = (u32*)surface->Pixels;
                    i32 stride = surface->Pitch / 4;
                    r += surface->W == 4 && surface->H == 3 && surface->Format == PixelFormat.Rgba32 && filled
                        && pixels[1 * stride + 1] == 0xFF00FF00u32 && pixels[0] == 0 ? 1000 : 0;
                    SDL3.Rect.Rect other;
                    other.X = 2;
                    other.W = 5;
                    other.H = 2;
                    SDL3.Rect.Rect both;
                    r += RectFunctions.GetRectIntersection(&rect, &other, &both) && both.X == 2 && both.Y == 1 && both.W == 1 && both.H == 1 ? 10000 : 0;
                    SurfaceFunctions.DestroySurface(surface);
                    Init.Quit();
                }
                return r;
            }
            """;
        var sdlCompiled = AntrilCompiler.Compile([.. sdlSources.Select(s => new SourceText(s.Value, s.Key)), new SourceText(sdlProgram, "main.ant")], [coreReference], "sdlshaped");
        Check("the SDL3 files and a program written against the shaped names compile together", sdlCompiled.Success, string.Join("; ", sdlCompiled.Diagnostics.Take(4)));
        byte[] sdlBytes = LvaContainer.Unwrap(File.ReadAllBytes(Path.Combine(sdlFolder, "SDL3.Bindings.dll")));
        var program = AntrilCompiler.Compile([new SourceText(sdlProgram, "main.ant")], [coreReference, LvaFormat.Read(sdlBytes)], "sdlshapedapp");
        if (program.Success)
        {
            string local = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
            if (!File.Exists(local) || new FileInfo(local).Length != new FileInfo(dll).Length)
                File.Copy(dll, local, overwrite: true);
            var linked = HirLinker.Link("sdlshapedapp", [coreBytes, sdlBytes, LvaFormat.Write(program.Assembly!)]);
            var lowered = linked.Success ? HirLowerer.Lower(linked.Assembly!) : null;
            var objectFile = lowered is { Success: true } ? Compiler.CompileObject(lowered.Module!) : null;
            var ir = objectFile is { Success: true } ? Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered!.FunctionName(linked.Assembly!.EntryPoint!.Value) }) : null;
            if (ir is { Success: true })
            {
                var (vm, llvm, x64, detail) = RunEverywhere(ir, ir.Entry!);
                Check("…and runs against the shaped SDL3.Bindings.dll on the VM, LLVM and x64 → 11111", vm == 11111 && llvm == 11111 && x64 == 11111, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
            }
            else
                Check("…links and lowers", false, string.Join("; ", (linked.Errors ?? []).Take(3)));
        }
        else
            Check("a program against the shaped SDL3.Bindings.dll compiles", false, string.Join("; ", program.Diagnostics.Take(4)));
    }

    // ── larva bind ──

    /// <summary>`larva bind` end to end: the .lva and the .ant, and a build that uses the generated source.</summary>
    [Fact]
    public void Larva_bind_writes_the_lva_and_the_ant_and_a_build_uses_them()
    {
        RequireWindows();
        string root = TestEnvironment.TempFolder("bind");
        File.WriteAllText(Path.Combine(root, "api.bind"), """
            library kernel32.dll
            GetCurrentProcessId() : u32
            GetTickCount64() : u64
            lstrlenW(String<u16> text) : i32
            """);
        string lva = Path.Combine(root, "Kernel32.lva"), ant = Path.Combine(root, "Kernel32.ant");
        var (code, text) = Larva("bind", "kernel32.dll", Path.Combine(root, "api.bind"), "-o", lva, "--ant", ant);
        Check("larva bind writes the .lva and the .ant", code == 0 && File.Exists(lva) && File.Exists(ant), text.Trim());
        if (code != 0)
            return;
        var fromLva = LvaFormat.Read(File.ReadAllBytes(lva));
        Check("the .lva holds the three imports", fromLva.Types.Count == 1 && fromLva.Types[0].Methods.Count == 3);
        File.WriteAllText(Path.Combine(root, "main.ant"), """
            import System;
            import Bindings;

            Main() : i32
            {
                Console.WriteLine((Kernel32.GetCurrentProcessId() > 0 ? 1 : 0) + (Kernel32.lstrlenW(new String<u16>(Unicode.ToUtf16("abc"))) == 3 ? 10 : 0));
                return 0;
            }
            """);
        string exe = Path.Combine(root, "app.exe");
        var (buildCode, buildText) = Larva("build", "-o", exe, "--backend", "x64", Path.Combine(root, "main.ant"), ant);
        Check("larva build with the generated Antril source links kernel32", buildCode == 0 && File.Exists(exe), buildText.Trim());
        if (buildCode != 0)
            return;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true })!;
        string output = process.StandardOutput.ReadToEnd().Split((char)10)[0].Trim();
        process.WaitForExit(30_000);
        Check("the program calls kernel32 through the generated bindings → 11", output == "11", output);
    }

    /// <summary>The library alone (K32.dll, from a C header), no Antril source for the bindings: as a `larva build` input, and as `assembly:` in a .queen's DEPS.</summary>
    [Fact]
    public void Larva_build_takes_a_bindings_library_as_input_and_as_assembly_in_DEPS()
    {
        RequireWindows();
        string root = TestEnvironment.TempFolder("bind");
        File.WriteAllText(Path.Combine(root, "api.h"), """
            #define K32_TAG 7
            typedef unsigned long DWORD;
            typedef unsigned __int64 ULONGLONG;
            __declspec(dllimport) DWORD __stdcall GetCurrentProcessId(void);
            __declspec(dllimport) ULONGLONG __stdcall GetTickCount64(void);
            __declspec(dllimport) int __stdcall lstrlenW(const wchar_t* lpString);
            """);
        string hLva = Path.Combine(root, "K32.dll"), hBind = Path.Combine(root, "derived.bind");
        var (hCode, hText) = Larva("bind", "kernel32.dll", Path.Combine(root, "api.h"), "-o", hLva, "--describe", hBind, "--no-pp");
        Assert.True(hCode == 0 && File.Exists(hLva), "larva bind reads the header: " + hText.Trim());
        File.WriteAllText(Path.Combine(root, "main2.ant"), """
            import System;
            import Bindings;

            Main() : i32
            {
                Console.WriteLine((Kernel32.GetCurrentProcessId() > 0 ? 1 : 0) + (Kernel32.lstrlenW(new String<u16>(Unicode.ToUtf16("abc"))) == 3 ? 10 : 0) + Kernel32.K32_TAG * 100);
                return 0;
            }
            """);
        string exe2 = Path.Combine(root, "app2.exe");
        var (code2, text2) = Larva("build", "-o", exe2, "--backend", "x64", Path.Combine(root, "main2.ant"), hLva);
        Check("larva build takes the bindings library (.dll) as an input, with no .ant for it", code2 == 0 && File.Exists(exe2), text2.Trim());
        if (code2 == 0)
            Check("…and the program runs → 711", Output(exe2) == "711", Output(exe2));
        string queenFolder = Path.Combine(root, "qapp");
        Directory.CreateDirectory(queenFolder);
        File.Copy(Path.Combine(root, "main2.ant"), Path.Combine(queenFolder, "main.ant"), overwrite: true);
        File.WriteAllText(Path.Combine(queenFolder, "qapp.queen"), "queen 1.0;\nchunk PROJ:\n   name: qapp\nchunk DEPS:\n   assembly: ../K32.dll\n");
        string exe3 = Path.Combine(root, "app3.exe");
        var (code3, text3) = Larva("build", "-o", exe3, "--backend", "llvm", Path.Combine(queenFolder, "qapp.queen"));
        Check("a .queen names the bindings library (.dll) as `assembly:` in DEPS", code3 == 0 && File.Exists(exe3), text3.Trim());
        if (code3 == 0)
            Check("…and the program runs (LLVM) → 711", Output(exe3) == "711", Output(exe3));
        File.WriteAllText(Path.Combine(queenFolder, "qapp.queen"), "queen 1.0;\nchunk PROJ:\n   name: qapp\nchunk DEPS:\n   assembly: ../Missing.dll\n");
        var (code4, text4) = Larva("build", "-o", exe3, Path.Combine(queenFolder, "qapp.queen"));
        Check("a missing library is an error naming it", code4 != 0 && text4.Contains("QN108") && text4.Contains("Missing.dll"), text4.Trim());
    }

    private static string Output(string exe)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true })!;
        string output = process.StandardOutput.ReadToEnd().Split((char)10)[0].Trim();
        process.WaitForExit(30_000);
        return output;
    }

    private static (int Code, string Output) Larva(params string[] arguments)
    {
        string cli = Repositories.LarvaCli;
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); // both pipes at once: a full one would stop the child
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(120_000);
        return (process.ExitCode, output);
    }

    /// <summary>Runs a linked program on the VM, LLVM and x64 (a copy of Larva.Tests' linker fixture).</summary>
    private static (long Vm, int Llvm, int X64, string Detail) RunEverywhere(LinkResult linked, string entry, GcConfig? programGc = null)
    {
        var compilation = new CompilationOptions { Gc = programGc ?? GcConfig.Default };
        long vm = -1; int llvm = -1, x64 = -1; string detail = "";
        try { vm = Convert.ToInt64(new Interpreter(linked.Il!).Run(entry)); } catch (Exception e) { detail += $"VM: {e.Message}; "; }
        global::Larva.Native.NativeRuntime.Reset();
        var compiled = global::Larva.Llvm.LlvmBackend.Compile(linked, new global::Larva.Llvm.LlvmOptions { Compilation = compilation });
        if (compiled.Success)
        {
            using var program = compiled.Program!;
            using var jit = program.Jit();
            llvm = ((delegate* unmanaged<int>)jit.Address(entry))();
            int retains = global::Larva.Native.NativeRuntime.Retains, releases = global::Larva.Native.NativeRuntime.Releases, errors = global::Larva.Native.NativeRuntime.Errors;
            detail += $"[llvm retains {retains} releases {releases} errors {errors}] ";
        }
        else
            detail += $"LLVM: {compiled}; ";
        global::Larva.Native.NativeRuntime.Reset();
        var native = global::Larva.X64.X64Backend.Compile(linked, new global::Larva.X64.X64Options { Compilation = compilation });
        if (native.Success)
        {
            using var image = native.Program!.Load();
            x64 = ((delegate* unmanaged<int>)image.Address(entry))();
        }
        else
            detail += $"x64: {native}; ";
        global::Larva.Native.NativeRuntime.Reset();
        return (vm, llvm, x64, detail);
    }
}
