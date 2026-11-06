using System.Diagnostics;
using Antril.Binding;
using Antril.Syntax;
using Larva.Hir.Lowering;
using Larva.Llvm;
using Larva.Runtime.Vm;
using Larva.X64;

namespace Antril.Tests;

/// <summary>
/// Item 9: interop with C (imports, exports, func*, pointers, Marshal) and reflection (Type, members, attributes,
/// typeof(T) and generic boxing): programs whose printed output must be the same on LVM, x64 and LLVM (and right).
/// Each is its own assembly, so reflection does not make the other tests' programs bigger.
/// </summary>
public sealed unsafe class InteropReflectionTests : CheckedTest
{
    [Fact]
    public void Interop_program_prints_alike_on_the_VM_x64_and_LLVM() => RunPrinting(CoreLibrary.Sources, "interop", InteropSource, InteropExpected);

    [Fact]
    public void Reflection_program_prints_alike_on_the_VM_x64_and_LLVM() => RunPrinting(CoreLibrary.Sources, "reflection", ReflectionSource, ReflectionExpected);

    [Fact]
    public void Enums_and_attributes_program_prints_alike_on_the_VM_x64_and_LLVM() =>
        RunPrinting(CoreLibrary.Sources, "enums and attributes", EnumsAttributesSource, EnumsAttributesExpected);

    [Fact]
    public void Wrong_interop_and_reflection_get_their_diagnostics() => InteropAndReflection(CoreLibrary.Sources);

    private void InteropAndReflection(List<SourceText> library)
    {
        (string Source, string Code, string What)[] wrong =
        [
            ("[Import(\"x.dll\")] F() : i32 { return 1; }", "AN1051", "an import with a body"),
            ("[Import] F() : i32;", "AN1052", "an import without its library"),
            ("class C { } [Import(\"x.dll\")] F(C c) : i32;", "AN1053", "an import taking an object"),
            ("[Import(\"x.dll\")] F() : str;", "AN1054", "an import giving a string"),
            ("[Export] G<T>(T x) : i32 => 1;", "AN1055", "a generic export"),
            ("[Export] G(str s) : i32 => 1;", "AN1056", "an export taking a string"),
            ("class C { } F(func*<C> : i32 f) { }", "AN1057", "a func* over an object type"),
            ("[Unknown] F() { }", "AN1058", "an attribute that is not a class"),
            ("class NotAttr { } [NotAttr] F() { }", "AN1058", "an attribute class not derived from Attribute"),
            ("class A : Attribute { public: new(i32 x) { } } [A(\"text\")] F() { }", "AN1060", "attribute arguments no constructor takes"),
            ("class A : Attribute { } [A(Missing = 1)] F() { }", "AN1061", "a named attribute argument that is not a member"),
            ("H(i32 x) : i32 => x; F() { func*<i64> : i32 f = &H; }", "AN2118", "&F with the wrong signature"),
            ("F(func*<i32> : i32 f) : i32 => f(1);", "AN2120", "calling a func* outside unsafe code"),
            ("F() { unsafe { i32* p = null; i64* q = null; i64 d = p - q; } }", "AN2122", "subtracting pointers to different types"),
            ("F() { i32* p = null; p = p + 1; }", "AN2121", "pointer arithmetic outside unsafe code"),
            ("[Import(\"x.dll\", \"Y\")] F(i32 a, str b, String<u16> c, void* d, func*<i32> : i32 e) : f64;", "", "an import with numbers, strings, pointers and func*"),
            ("F<T>(T x) : any => x; G<T>(any x) : T => (T)x;", "", "a type parameter's value as any and back"),
            ("public value P { public: i32 x; } public enum E : P { A }", "AN1019", "a member of a value-based enum without its value"),
            ("public value P { public: i32 x; } public bitflag enum E : P { A }", "AN1064", "a bitflag enum over a value"),
            ("public class C { } public enum E : C { A }", "AN1006", "an enum over a class"),
        ];
        foreach (var (text, code, what) in wrong)
        {
            var result = AntrilCompiler.Compile([.. library, new SourceText("import System;\nimport System.Reflection;\n" + text, "wrong.ant")], "Wrong");
            if (code.Length == 0)
            {
                Check($"accepted: {what}", result.Success, string.Join("; ", result.Diagnostics.Take(4)));
                continue;
            }
            Check($"{code}: {what}", !result.Success && result.Diagnostics.Any(d => d.Code == code),
                string.Join("; ", result.Diagnostics.Take(4).Select(d => d.ToString())));
        }
    }

    /// <summary>What a program prints: its Main run on the three back ends, each output compared with the expected one.</summary>
    private void RunPrinting(List<SourceText> library, string name, string source, string expected)
    {
        var watch = Stopwatch.StartNew();
        var compiled = AntrilCompiler.Compile([.. library, new SourceText(source, name + ".ant")], "Item9");
        Check($"the {name} program compiles", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(6)));
        if (!compiled.Success)
            return;
        long frontend = watch.ElapsedMilliseconds;
        watch.Restart();
        var lowered = HirLowerer.Lower(compiled.Assembly!);
        Check($"the {name} program lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(6)));
        if (!lowered.Success)
            return;
        long lowering = watch.ElapsedMilliseconds;
        watch.Restart();
        var vm = Larva.IR.Compiler.Compile(lowered.Module!);
        long checks = watch.ElapsedMilliseconds;
        Check($"its IR passes the checks", vm.Success, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        watch.Restart();
        var x64 = X64Backend.Compile(lowered.Module!);
        long x64Ms = watch.ElapsedMilliseconds;
        watch.Restart();
        var llvm = LlvmBackend.Compile(lowered.Module!, new LlvmOptions { OptimizationLevel = 0 });
        long llvmMs = watch.ElapsedMilliseconds;
        Check("x64 and LLVM compile it", x64.Success && llvm.Success, $"{x64.Error} {llvm}");
        if (!vm.Success || !x64.Success || !llvm.Success)
            return;
        string main = lowered.Module!.Functions.First(f => f.Name.EndsWith("$Module.Main") && !f.Name.StartsWith("System.")).Name;
        expected = expected.Replace("\r\n", "\n");
        var onVm = Capture(() =>
        {
            var interpreter = new Interpreter(vm.Il!);
            interpreter.Run(HirLowerer.StaticInitName);
            interpreter.Run(main);
        });
        Check($"{name} on LVM", onVm == expected, Difference(onVm, expected));
        using (var image = x64.Program!.Load())
        {
            var onX64 = Capture(() =>
            {
                ((delegate* unmanaged<void>)image.Address(HirLowerer.StaticInitName))();
                ((delegate* unmanaged<void>)image.Address(main))();
            });
            Check($"{name} on x64", onX64 == expected, Difference(onX64, expected));
        }
        using var program = llvm.Program!;
        using var jit = program.Jit();
        var onLlvm = Capture(() =>
        {
            ((delegate* unmanaged<void>)jit.Address(HirLowerer.StaticInitName))();
            ((delegate* unmanaged<void>)jit.Address(main))();
        });
        Check($"{name} on LLVM", onLlvm == expected, Difference(onLlvm, expected));
    }

    private static string Capture(Action run)
    {
        var old = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            run();
        }
        catch (Exception e)
        {
            writer.Write($"[{e.GetType().Name}: {e.Message}]");
        }
        finally
        {
            Console.SetOut(old);
        }
        return writer.ToString().Replace("\r\n", "\n");
    }

    /// <summary>The first line that differs.</summary>
    private static string Difference(string actual, string expected)
    {
        var a = actual.Split('\n');
        var e = expected.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, e.Length); i++)
        {
            string have = i < a.Length ? a[i] : "(end)", want = i < e.Length ? e[i] : "(end)";
            if (have != want)
                return $"line {i + 1}: \"{have}\", expected \"{want}\"";
        }
        return "";
    }

    internal const string InteropSource = """"
        import System;
        import System.Runtime.InteropServices;

        into namespace InteropTest
        {
        	[Import("ucrtbase.dll")] abs(i32 x) : i32;
        	[Import("ucrtbase.dll", "strlen")] CLength(str text) : u64;
        	[Import("ucrtbase.dll")] sqrt(f64 x) : f64;
        	[Import("kernel32.dll")] GetCurrentProcessId() : u32;
        	[Import("kernel32.dll")] lstrlenW(String<u16> text) : i32;
        	[Import("kernel32.dll")] GetModuleHandleW(String<u16> name) : void*;
        	[Import("kernel32.dll")] GetProcAddress(void* module, str name) : func*<i32> : i32;
        	[Import("ucrtbase.dll")] qsort(void* items, u64 count, u64 size, func*<void*, void*> : i32 compare);
        	[Import("ucrtbase.dll", Entry = "memset")] Fill(void* block, i32 value, u64 bytes) : void*;

        	unsafe Compare(void* a, void* b) : i32
        	{
        		i32 x = *(i32*)a;
        		i32 y = *(i32*)b;
        		return x < y ? -1 : x > y ? 1 : 0;
        	}

        	Twice(i32 x) : i32 => x * 2;

        	value Pair
        	{
        		public:
        			i32 A;
        			i64 B;
        	}

        	class Counter<T>
        	{
        		static i32 made;
        		public:
        			new() { made++; }
        			static Made() : i32 => made;
        	}

        	Main()
        	{
        		Console.WriteLine(abs(-42));
        		Console.WriteLine(CLength("héllo"));
        		Console.WriteLine(sqrt(2.25));
        		Console.WriteLine(GetCurrentProcessId() > 0);
        		Console.WriteLine(lstrlenW(new String<u16>(Unicode.ToUtf16("año 2026"))));
        		unsafe
        		{
        			i32* items = (i32*)Marshal.AllocHGlobal(5 * 4);
        			items[0] = 5; items[1] = 3; items[2] = 9; items[3] = 1; items[4] = 7;
        			qsort(items, 5, 4, Compare);
        			str line = "";
        			loop (i32 i = 0..5)
        				line = line + items[i] + " ";
        			Console.WriteLine(line);
        			i32* last = items + 4;
        			Console.WriteLine((last - items) + " " + *last);
        			last--;
        			last -= 1;
        			Console.WriteLine(last[0] + " " + (items < last) + " " + (last == items + 2));
        			Fill(items, 0, 20);
        			Console.WriteLine(items[2]);
        			Marshal.WriteInt32(items, 8, 1234);
        			Marshal.WriteByte(items, 0, 7);
        			Console.WriteLine(Marshal.ReadInt32(items, 8) + " " + Marshal.ReadByte(items));
        			Marshal.FreeHGlobal(items);
        			u8* text = Marshal.StringToCoTaskMemUTF8("hola C");
        			Console.WriteLine(Marshal.PtrToStringUTF8(text) + " " + Marshal.PtrToStringUTF8(text, 4));
        			Marshal.FreeCoTaskMem(text);
        			u16* wide = Marshal.StringToHGlobalUni("ñandú");
        			Console.WriteLine(Marshal.PtrToStringUni(wide));
        			Marshal.FreeHGlobal(wide);
        			func*<i32> : i32 twice = &Twice;
        			Console.WriteLine(twice(21));
        			func*<i32> : i32 cAbs = GetProcAddress(GetModuleHandleW(new String<u16>(Unicode.ToUtf16("ucrtbase.dll"))), "abs");
        			Console.WriteLine(cAbs(-17));
        			i32* none = null;
        			Console.WriteLine(none == null);
        			i32[] numbers = [10, 20, 30];
        			i32* inside = &numbers[1];
        			Console.WriteLine(*inside + inside[1]);
        			Pair* pair = (Pair*)NativeMemory.AllocZeroed(1, (u64)sizeof(Pair));
        			pair->A = 3;
        			pair->B = 40;
        			Console.WriteLine(pair->A + pair->B + " " + sizeof(Pair) + " " + Marshal.SizeOf<Pair>());
        			i64[] wideNumbers = [1, 2, 3];
        			Marshal.Copy<i64>(wideNumbers, 0, pair, 2);
        			Console.WriteLine(pair->A + " " + pair->B);
        			NativeMemory.Free(pair);
        		}
        		new Counter<i32>();
        		new Counter<i32>();
        		new Counter<str>();
        		Console.WriteLine(Counter<i32>.Made() + " " + Counter<str>.Made() + " " + Counter<f64>.Made());
        	}
        }
        """";

    private const string InteropExpected = """"
        42
        6
        1.5
        true
        8
        1 3 5 7 9 
        4 9
        5 true true
        0
        1234 7
        hola C hola
        ñandú
        42
        17
        true
        50
        43 16 16
        1 2
        2 1 0
        """" + "\n";

    /// <summary>
    /// A value-based enum (`enum Shade : Rgb`: `(Rgb)shade`, `(Shade)rgb`, a value no member has), enums printed through
    /// `any` (their names), and attributes on enum members, fields of classes and values, constructors, properties,
    /// methods and parameters, read back through reflection.
    /// </summary>
    internal const string EnumsAttributesSource = """"
        import System;
        import System.Reflection;

        into namespace EnumAttr
        {
        	public record value Rgb
        	{
        		public:
        			u8 R;
        			u8 G;
        			u8 B;
        			new(u8 R, u8 G, u8 B);
        	}

        	public value Plain { public: i32 x; new(i32 x); }

        	public enum Shade : Rgb
        	{
        		Red = new Rgb(255, 0, 0),
        		[Tag("verde")] Green = new Rgb(0, 255, 0),
        	}

        	public enum Size : Plain { Small = new Plain(1), Big = new Plain(10) }

        	public enum Day { Lunes, Martes }

        	public enum Word : str { Hola, Chau = "adios" }

        	public class TagAttribute : Attribute
        	{
        		public:
        			str text;
        			new(str text) { this.text = text; }
        	}

        	public value Point
        	{
        		public:
        			[Tag("x")] i32 x;
        			i32 y;
        	}

        	public class Shop
        	{
        		[Tag("count")] i32 count;
        		public:
        			[Tag("ctor")] new([Tag("start")] i32 start) { count = start; }
        			[Tag("prop")] i32 Count { get => count; }
        			[Tag("method")] Add([Tag("amount")] i32 amount, str note) : i32 => amount;
        	}

        	Tags(any[] found) : str
        	{
        		str text = "";
        		loop (any a in found)
        		{
        			if (a is TagAttribute tag)
        				text = text + "[" + tag.text + "]";
        		}
        		return text;
        	}

        	Main()
        	{
        		Shade shade = Shade.Green;
        		Rgb rgb = (Rgb)shade;
        		Console.WriteLine(rgb.G + " " + ((Plain)Size.Big).x);
        		Console.WriteLine(shade);
        		Console.WriteLine((Shade)new Rgb(255, 0, 0));
        		Console.WriteLine((i32)(Shade)new Rgb(1, 2, 3));
        		any day = Day.Martes;
        		Console.WriteLine(day.ToString() + " " + Word.Chau + " " + (str)Word.Chau);
        		BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        		loop (FieldInfo f in typeof(Shade).GetFields(all))
        			Console.WriteLine("enum " + f.Name + Tags(f.GetCustomAttributes()));
        		loop (FieldInfo f in typeof(Point).GetFields())
        			Console.WriteLine("value field " + f.Name + Tags(f.GetCustomAttributes()));
        		loop (FieldInfo f in typeof(Shop).GetFields(all))
        			Console.WriteLine("field " + f.Name + Tags(f.GetCustomAttributes()));
        		loop (ConstructorInfo c in typeof(Shop).GetConstructors())
        		{
        			Console.WriteLine("constructor" + Tags(c.GetCustomAttributes()));
        			loop (ParameterInfo p in c.GetParameters())
        				Console.WriteLine("  " + p.Name + Tags(p.GetCustomAttributes()));
        		}
        		Console.WriteLine("property" + Tags(typeof(Shop).GetProperty("Count")?.GetCustomAttributes() ?? []));
        		MethodInfo? add = typeof(Shop).GetMethod("Add");
        		Console.WriteLine("method" + Tags(add?.GetCustomAttributes() ?? []));
        		loop (ParameterInfo p in add?.GetParameters() ?? [])
        			Console.WriteLine("  " + p.Name + Tags(p.GetCustomAttributes()) + " " + p.IsDefined(typeof(TagAttribute)));
        	}
        }
        """";

    private const string EnumsAttributesExpected = """"
        255 10
        Green
        Red
        -1
        Martes Chau adios
        enum Red
        enum Green[verde]
        value field x[x]
        value field y
        field count[count]
        constructor[ctor]
          start[start]
        property[prop]
        method[method]
          amount[amount] true
          note false

        """";

    internal const string ReflectionSource = """"
        import System;
        import System.Reflection;
        import System.Collections.Generic;

        into namespace ReflTest
        {
        	public enum Color { Red, Green = 5, Blue }

        	public interface IShape { Area() : f64; }

        	public class Animal
        	{
        		public:
        			str name;
        			i32 legs;
        			static i32 created;
        			new(str name, i32 legs) { this.name = name; this.legs = legs; created++; }
        			virtual Speak() : str => "...";
        			Describe(str prefix) : str => prefix + name + " (" + legs + ")";
        			i32 Legs { get => legs; set => legs = value; }
        	}

        	public class Dog : Animal, IShape
        	{
        		public:
        			bool good;
        			new(str name) : base(name, 4) { good = true; }
        			override Speak() : str => "Woof";
        			Area() : f64 => 1.5;
        	}

        	public value Point
        	{
        		public:
        			i32 X;
        			i32 Y;
        			new(i32 x, i32 y) { X = x; Y = y; }
        			Sum() : i32 => X + Y;
        	}

        	public class Box<T>
        	{
        		public:
        			T item;
        			new(T item) { this.item = item; }
        			Get() : T => item;
        	}

        	public enum Level { Low, High }

        	public class DescriptionAttribute : Attribute
        	{
        		public:
        			str Text;
        			i32 Priority;
        			Level Level { get; set; }
        			Type? Related;
        			new(str text) { Text = text; }
        			override ToString() : str => "Description(" + Text + ", " + Priority + ", " + (i32)Level + ", " + (Related?.Name ?? "-") + ")";
        	}

        	public class Marker : Attribute { }

        	[Description("a thing", Priority = 3, Level = Level.High, Related = typeof(i64))]
        	[Marker]
        	public class Thing
        	{
        		public:
        			[Description("its size")] i32 Size;
        			[Marker] Grow(i32 by) { Size += by; }
        			[Description("its name", Priority = -1)] str Name { get; set; }
        			new() { Name = "thing"; }
        	}


        	NameOf<T>() : str => typeof(T).FullName;

        	Boxed<T>(T value) : any => value;

        	Back<T>(any value) : T => (T)value;

        	Main()
        	{
        		Type dogType = typeof(Dog);
        		Console.WriteLine(dogType.Name + " " + dogType.FullName + " " + dogType.IsClass);
        		Console.WriteLine(dogType.BaseType?.Name ?? "none");
        		Console.WriteLine(typeof(i32).FullName + " " + typeof(i32).IsPrimitive + " " + typeof(i32).IsValueType);
        		Console.WriteLine(typeof(str).FullName);
        		Console.WriteLine(typeof(Box<i32>).FullName + " " + typeof(Box<i32>).GetGenericArguments()[0].Name);
        		Console.WriteLine(typeof(i32[]).FullName + " " + typeof(Dog[,]).FullName + " " + typeof(i32?).FullName);
        		Console.WriteLine(typeof(Dog) == new Dog("Rex").GetType());
        		Console.WriteLine(typeof(Animal) == new Dog("Rex").GetType());
        		any p = new Point(2, 3);
        		Console.WriteLine(p.GetType().FullName);
        		any seven = 7;
        		Console.WriteLine(seven.GetType().Name);
        		Console.WriteLine(NameOf<i64>() + " " + NameOf<Dog>() + " " + NameOf<List<str>>() + " " + NameOf<f64[]>());
        		Console.WriteLine(typeof(IShape).IsAssignableFrom(typeof(Dog)) + " " + typeof(Animal).IsAssignableFrom(typeof(Dog)) + " " + typeof(Dog).IsSubclassOf(typeof(Animal)));
        		loop (str n in typeof(Color).GetEnumNames())
        			Console.Write(n + " ");
        		Console.WriteLine();
        		loop (i64 v in typeof(Color).GetEnumValues())
        			Console.Write(v + " ");
        		Console.WriteLine();
        		Dog rex = new Dog("Rex");
        		loop (FieldInfo f in typeof(Dog).GetFields())
        			Console.WriteLine("field " + f.Name + " : " + f.FieldType.Name + " = " + (f.GetValue(rex)?.ToString() ?? "null"));
        		FieldInfo? legs = typeof(Dog).GetField("legs");
        		legs?.SetValue(rex, 3);
        		Console.WriteLine(rex.legs);
        		MethodInfo? speak = typeof(Animal).GetMethod("Speak");
        		Console.WriteLine(speak?.Invoke(rex, [])?.ToString() ?? "null");
        		MethodInfo? describe = typeof(Dog).GetMethod("Describe");
        		Console.WriteLine(describe?.Invoke(rex, ["a dog: "])?.ToString() ?? "null");
        		PropertyInfo? legsProperty = typeof(Animal).GetProperty("Legs");
        		legsProperty?.SetValue(rex, 5);
        		Console.WriteLine(legsProperty?.GetValue(rex)?.ToString() ?? "null");
        		any made = Activator.CreateInstance(typeof(Dog), ["Fido"]);
        		if (made is Dog fido)
        			Console.WriteLine(fido.name + " " + fido.Speak());
        		any point = Activator.CreateInstance(typeof(Point), [10, 20]);
        		Console.WriteLine(typeof(Point).GetMethod("Sum")?.Invoke(point, [])?.ToString() ?? "null");
        		Console.WriteLine(typeof(Point).GetField("X")?.GetValue(point)?.ToString() ?? "null");
        		Console.WriteLine(Boxed<i32>(41).GetType().Name + " " + Back<i32>(Boxed<i32>(41)) + " " + Back<str>(Boxed<str>("hey")));
        		Box<str> b = new Box<str>("inside");
        		Console.WriteLine(typeof(Box<str>).GetField("item")?.GetValue(b)?.ToString() ?? "null");
        		Console.WriteLine(typeof(Animal).GetField("created")?.GetValue(null)?.ToString() ?? "null");
        		loop (MethodInfo m in typeof(Dog).GetMethods())
        			Console.Write(m.Name + " ");
        		Console.WriteLine();
        		loop (ConstructorInfo c in typeof(Animal).GetConstructors())
        			Console.WriteLine(c.ToString());

        		// attributes
        		loop (any a in typeof(Thing).GetCustomAttributes())
        			Console.WriteLine(a.ToString() + " " + a.GetType().Name);
        		Console.WriteLine(typeof(Thing).IsDefined(typeof(Marker)) + " " + typeof(Thing).GetCustomAttributes(typeof(DescriptionAttribute)).Length);
        		loop (any a in typeof(Thing).GetField("Size")?.GetCustomAttributes() ?? [])
        			Console.WriteLine("Size: " + a.ToString());
        		Console.WriteLine("Grow marked: " + (typeof(Thing).GetMethod("Grow")?.IsDefined(typeof(Marker)) ?? false));
        		loop (any a in typeof(Thing).GetProperty("Name")?.GetCustomAttributes() ?? [])
        			Console.WriteLine("Name: " + a.ToString());
        		Console.WriteLine(typeof(func<i32, str> : bool).FullName + " " + typeof(func<i32> : void).FullName);
        		Console.WriteLine(typeof(Point?).FullName + " " + typeof(Point?).IsNullable + " " + typeof(Point?).GetGenericArguments()[0].Name);
        		Console.WriteLine(typeof(Box<Dog>).GetGenericArguments()[0].BaseType?.Name ?? "-");
        		Console.WriteLine(typeof(Dog).GetInterfaces().Length + " " + typeof(Dog).GetInterfaces()[0].Name + " " + typeof(IShape).IsInterface);
        		any? nothing = null;
        		Console.WriteLine(typeof(Dog).IsInstanceOfType(rex) + " " + typeof(Dog).IsInstanceOfType(nothing) + " " + typeof(IShape).IsInstanceOfType(rex));
        	}
        }
        """";

    private const string ReflectionExpected = """"
        Dog ReflTest.Dog true
        Animal
        System.Int32 true true
        System.String
        ReflTest.Box<System.Int32> Int32
        System.Int32[] ReflTest.Dog[,] System.Int32?
        true
        false
        ReflTest.Point
        Int32
        System.Int64 ReflTest.Dog System.List<System.String> System.Float64[]
        true true true
        Red Green Blue 
        0 5 6 
        field good : Boolean = true
        field name : String = Rex
        field legs : Int32 = 4
        3
        Woof
        a dog: Rex (3)
        5
        Fido Woof
        30
        10
        Int32 41 hey
        inside
        4
        Speak Area Describe GetType ToString Hash 
        Void .ctor(System.String, System.Int32)
        Description(a thing, 3, 1, Int64) DescriptionAttribute
        System.Any Marker
        true 1
        Size: Description(its size, 0, 0, -)
        Grow marked: true
        Name: Description(its name, -1, 0, -)
        func<System.Int32, System.String> : System.Boolean func<System.Int32> : System.Void
        ReflTest.Point? true Point
        Animal
        1 IShape true
        true false true
        """" + "\n";
}
