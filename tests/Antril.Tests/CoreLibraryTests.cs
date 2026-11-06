using System.Diagnostics;
using Antril.Binding;
using Antril.Syntax;
using Larva.Hir.Lowering;
using Larva.IR;
using Larva.Llvm;
using Larva.Runtime.Vm;
using Larva.X64;

namespace Antril.Tests;

/// <summary>
/// Phase 4 of the HIR rebuild: System.Core written in Antril (str, any, the primitives' members, StringBuilder, List,
/// Dictionary, HashSet, Console, exceptions) compiled with a program, the same results on LVM, x64 and LLVM, and how fast
/// its programs run next to .NET.
/// </summary>
public sealed unsafe class CoreLibraryTests : CheckedTest
{
    /// <summary>System.Core and the program of cases compiled together, once for the run (and how long it took).</summary>
    private static readonly Lazy<(AntrilResult Result, long Ms)> program = new(() =>
    {
        var watch = Stopwatch.StartNew();
        var compiled = AntrilCompiler.Compile([.. CoreLibrary.Sources, new SourceText(ProgramSource, "core.ant")], "CoreTest");
        return (compiled, watch.ElapsedMilliseconds);
    });

    /// <summary>System.Core's sources (the run stops here when they are not found).</summary>
    private List<SourceText> Library()
    {
        Check("Library/System.Core is found", CoreLibrary.Sources.Count > 0);
        Require();
        return CoreLibrary.Sources;
    }

    [Fact]
    public void Programs_run_alike_on_the_VM_x64_and_LLVM() => Programs(Library());

    [Fact]
    public void The_same_cases_with_reference_counting_on_x64_and_LLVM() => RunCounted(CompiledProgram(Library()).Assembly!);

    [Fact]
    public void Linked_against_System_Core_lva() => Linked(Library());

    [Fact]
    public void Wrong_generics_interfaces_and_properties_get_their_diagnostics() => Diagnostics(Library());

    [Fact]
    public void Access_across_assemblies() => Friends(Library());

    [Fact]
    public void Executables_using_System_Core_give_the_right_results() => Executables(Library());

    [Fact]
    public void Destructors_per_GC_mode() => Destructors(Library());

    internal const string ProgramSource = """
        import System;
        import System.IO;
        import System.Text;
        import System.Diagnostics;
        import System.Threading;
        import System.Threading.Tasks;
        import System.Reactive;

        into namespace CoreTest
        {
            public interface IFa { A() : i32; }
            public interface IFb { B() : i32; }
            public interface IFc { C() : i32; }
            public interface IFd { D() : i32; }
            public interface IFe { E() : i32; }
            public interface IFf { F() : i32; }
            public interface IFg { G() : i32; }
            public interface IFh { H() : i32; }
            public interface IFi { I() : i32; }
            public interface IFj { J() : i32; }
            public interface IPut<T> { Put(T x) : i32; }

            public class ManyFaces : IFa, IFb, IFc, IFd, IFe, IFf, IFg, IFh, IFi, IFj
            {
                public:
                    A() : i32 => 1;
                    B() : i32 => 2;
                    C() : i32 => 3;
                    D() : i32 => 4;
                    E() : i32 => 5;
                    F() : i32 => 6;
                    G() : i32 => 7;
                    H() : i32 => 8;
                    I() : i32 => 9;
                    virtual J() : i32 => 10;
            }

            public class MoreFaces : ManyFaces, IFj { public: override J() : i32 => 100; }

            public final class Packed<T : [u8, u16, u32]>
            {
                u64 tag;
                fixed T[] items;
                public:
                    new(T[] from) : fixed(from.Length)
                    {
                        loop (i32 i = 0..from.Length)
                            items[i] = from[i];
                        tag = 7;
                    }
                    new(i32 count, T value) : fixed(count)
                    {
                        loop (i32 i = 0..count)
                            items[i] = value;
                    }
                    i32 Count { get => items.Length; }
                    Sum() : u64
                    {
                        u64 total = tag;
                        loop (i32 i = 0..items.Length)
                            total += (u64)items[i];
                        return total;
                    }
            }

            public class PutBoth : IPut<i32>, IPut<str>
            {
                public:
                    Put(i32 x) : i32 => x;
                    Put(str x) : i32 => x.Length;
            }

            public value SmallFace : IFa { public: i32 v; new(i32 v) { this.v = v; } A() : i32 => v; }

            public class Point2
            {
                i32 x;
                public:
                    new(i32 x) { this.x = x; }
                    override ToString() : str => "P" + x;
            }

            public attribute Note { public: str text; new(str text) { this.text = text; } }
            public exception Missing { public: new(str message) : base(message) { } }
            public exception Gone : Missing { public: new() : base("gone") { } }
            public class Placed into namespace Inner { public: static Value() : i32 => 3; }
            Doubled(i32 x) : i32 into namespace Inner => x * 2;

            public class Box<T>
            {
                T value;
                public:
                    new(T value) { this.value = value; }
                    Get() : T => value;
            }

            public base class Holder<T>
            {
                T item;
                public:
                    new(T item) { this.item = item; }
                    virtual Describe() : i32 => 1;
                    Item() : T => item;
            }

            public class IntHolder : Holder<i32>
            {
                public:
                    new(i32 v) : base(v) { }
                    override Describe() : i32 => Item() * 2;
            }

            public Max<T : IComparable<T>>(T a, T b) : T => a.CompareTo(b) >= 0 ? a : b;

            public class Counter2
            {
                public:
                    i32 Value { get; set; }
                    static i32 Made { get; set; }
                    new() { Made += 1; }
            }

            public interface IShape
            {
                Area() : i32;
            }

            public class Sq : IShape
            {
                i32 s;
                public:
                    new(i32 s) { this.s = s; }
                    Area() : i32 => s * s;
            }

            public value Rect : IShape
            {
                i32 w, h;
                public:
                    new(i32 w, i32 h) { this.w = w; this.h = h; }
                    Area() : i32 => w * h;
            }

            public TotalArea<T : IShape>(List<T> shapes) : i32
            {
                i32 total = 0;
                loop (T shape in shapes)
                    total += shape.Area();
                return total;
            }

            public Properties() : i32
            {
                Counter2 a = new Counter2();
                Counter2 b = new Counter2();
                a.Value = 5;
                a.Value += 3;
                b.Value = a.Value * 2;
                return a.Value + b.Value * 10 + Counter2.Made * 1000 + (Int32.MaxValue == 2147483647 ? 10000 : 0);
            }

            public Interfaces() : i32
            {
                List<Sq> squares = new List<Sq>();
                squares.Add(new Sq(3));
                squares.Add(new Sq(4));
                List<Rect> rects = new List<Rect>();
                rects.Add(new Rect(2, 5));
                return TotalArea(squares) + TotalArea(rects) * 100;
            }

            public Concat() : i32
            {
                str a = "hello";
                str b = a + " world";
                return b.Length;
            }

            public Equality() : i32 => ("abc" == "ab" + "c" ? 1 : 0) + ("abc" != "abd" ? 10 : 0) + ("abc" == "abcd" ? 100 : 0);

            public Trimming() : i32
            {
                str t = "  Hello World  ".Trim();
                return t.ToUpper().IndexOf("WORLD") + t.Length * 10;
            }

            public Substrings() : i32
            {
                str s = "abcdef";
                return (s.Substring(2, 3) == "cde" ? 1 : 0) + (s.StartsWith("abc") ? 10 : 0) + (s.EndsWith("ef") ? 100 : 0)
                    + (s.Contains("cd") ? 1000 : 0) + s.IndexOf('d') * 10000;
            }

            public Numbers() : i32
            {
                i32 x = -987;
                i64 big = 9223372036854775807;
                return x.ToString().Length + (12345).ToString().Length * 10 + big.ToString().Length * 100
                    + Int64.MinValue.ToString().Length * 1000 + Int32.Parse("-4321");
            }

            public Builder() : i32
            {
                StringBuilder sb = new StringBuilder();
                loop (i32 i = 0..100)
                    sb.Append(i);
                sb.Append("!");
                return sb.ToString().Length;
            }

            public Lists() : i32
            {
                List<i32> list = new List<i32>();
                loop (i32 i = 0..10)
                    list.Add(i * i);
                i32 total = 0;
                loop (i32 x in list)
                    total += x;
                return total + list[3];
            }

            public ListOps() : i32
            {
                List<i32> list = new List<i32>();
                loop (i32 i = 1..=5)
                    list.Add(i);
                list.Insert(0, 10);
                list.RemoveAt(2);
                i32 popped = list.Pop();
                list[1] = 7;
                i32 sum = 0;
                loop (i32 x in list)
                    sum += x;
                return sum * 10 + list.Count + popped * 1000;
            }

            public StringList() : i32
            {
                List<str> words = new List<str>();
                words.Add("a");
                words.Add("bb");
                words.Add("ccc");
                i32 total = 0;
                loop (str w in words)
                    total += w.Length;
                return total * 10 + String.Join("-", words).Length;
            }

            public Maps() : i32
            {
                Dictionary<str, i32> map = new Dictionary<str, i32>();
                map["one"] = 1;
                map["two"] = 2;
                map.Set("three", 3);
                return map["two"] * 10 + map.Count;
            }

            public IntMap() : i32
            {
                Dictionary<i32, i32> map = new Dictionary<i32, i32>();
                loop (i32 i = 0..1000)
                    map[i] = i * 2;
                loop (i32 i = 0..1000)
                {
                    if (i % 2 == 0)
                        map.Remove(i);
                }
                i32 sum = 0;
                loop (i32 i = 0..1000)
                    sum += map.TryGet(i, 0);
                return sum + map.Count;
            }

            public MapIterate() : i32
            {
                Dictionary<str, i32> map = new Dictionary<str, i32>();
                map["a"] = 1;
                map["b"] = 2;
                map["c"] = 3;
                i32 total = 0, keys = 0;
                loop (KeyValuePair<str, i32> pair in map)
                {
                    total += pair.Value;
                    keys += pair.Key.Length;
                }
                return total * 10 + keys;
            }

            public Sets() : i32
            {
                HashSet<str> set = new HashSet<str>();
                i32 repeated = 0;
                if (!set.Add("x")) repeated++;
                if (!set.Add("y")) repeated++;
                if (!set.Add("x")) repeated++;
                if (!set.Add("z")) repeated++;
                if (!set.Add("y")) repeated++;
                return set.Count * 10 + repeated + (set.Contains("z") ? 100 : 0);
            }

            public Splitting() : i32
            {
                List<str> pieces = "a,b,,c".Split(',');
                return pieces.Count + (pieces[3] == "c" ? 10 : 0) + (pieces[2].IsEmpty ? 100 : 0);
            }

            public Errors() : i32
            {
                i32 r = 0;
                List<i32> list = new List<i32>();
                try { list.At(10); }
                catch (IndexError e) { r += 1; }
                Dictionary<str, i32> map = new Dictionary<str, i32>();
                try { map.Get("none"); }
                catch (KeyNotFoundError) { r += 10; }
                try { Number.Parse("x1"); }
                catch (FormatError e) { r += e.Message.Length * 100; }
                return r;
            }

            public Generic() : i32 => Max(3, 7) + Max("pear", "apple").Length * 10;

            public StringLoop() : i32
            {
                i32 count = 0;
                loop (u16 c in "hello world")
                {
                    if (c == 'l')
                        count++;
                }
                return count;
            }

            public ToStrings() : i32 => ("v=" + new Point2(42)).Length * 10 + ("n" + 5).Length;

            public GenericClass() : i32 => new Box<str>("abc").Get().Length + new Box<i32>(5).Get() * 10;

            public Inherit() : i32
            {
                Holder<i32> h = new IntHolder(21);
                return h.Describe();
            }

            public MathFns() : i32 => (i32)(Math.Sqrt(2.0) * 1000000) + (i32)Math.Round(2.5) * 10 + (i32)Math.Round(3.5) * 100
                + (i32)Math.Floor(-1.5) + (i32)Math.Pow(2, 10) + (i32)(Math.Sin(Math.PI / 2) * 1000) + Math.Abs(-5) + Math.Max(3, 9)
                + Math.Clamp(15, 0, 10) + (i32)Math.ISqrt(1000000) + (i32)Math.Gcd(84, 36);

            static Rethrow<T : exception>(T e) { throw e; }
            static Notes<T : attribute>(T a) : i32 => 1;

            const i64 Widest = Int32.MaxValue + 1i64;
            const u8 Byte = UInt8.MaxValue;

            public FixedItables() : i32
            {
                i32 r = 0;
                // Ten interfaces on eight fixed entries (some share one: found by the search), one redone in a derived class.
                List<any> all = new List<any>();
                all.Add(new ManyFaces());
                all.Add(new MoreFaces());
                i32 sum = 0;
                loop (any o in all)
                {
                    IFa a = (IFa)o; IFb b = (IFb)o; IFc c = (IFc)o; IFd d = (IFd)o; IFe e = (IFe)o;
                    IFf f = (IFf)o; IFg g = (IFg)o; IFh h = (IFh)o; IFi i = (IFi)o; IFj j = (IFj)o;
                    sum += a.A() + b.B() + c.C() + d.D() + e.E() + f.F() + g.G() + h.H() + i.I() + j.J();
                }
                r += sum == 200 ? 1 : 0;
                // Two instances of one generic interface: one entry, the other by the search; `is` checks the id.
                PutBoth both = new PutBoth();
                IPut<i32> pi = both;
                IPut<str> ps = both;
                any x = both;
                r += pi.Put(40) + ps.Put("ab") == 42 ? 10 : 0;
                r += x is IPut<i32> && x is IPut<str> && !(x is IPut<f64>) && !(x is IFa) ? 100 : 0;
                IFa small = new SmallFace(7);
                r += small.A() == 7 ? 1000 : 0;
                return r;
            }

            public InPlaceArrays() : i32
            {
                i32 r = 0;
                // A program's own class with an array kept in place: made by either constructor, many of them (collections).
                u8[] bytes = [1, 2, 3, 250];
                Packed<u8> a = new Packed<u8>(bytes);
                Packed<u32> b = new Packed<u32>(5, 100000u32);
                List<Packed<u16>> many = new List<Packed<u16>>();
                loop (i32 i = 0..3000)
                    many.Add(new Packed<u16>(i % 50, (u16)i));
                u64 total = 0;
                loop (Packed<u16> p in many)
                    total += p.Sum();
                r += a.Count == 4 && a.Sum() == 263 && b.Sum() == 500000 && total == 110838000 ? 1 : 0;
                // Strings keep their units in place: long ones (copied in memory), short ones, a copy that does not share them.
                str longer = "abcdefghijklmnopqrstuvwxyz0123456789";
                str joined = longer + longer;
                r += joined.Length == 72 && joined.Substring(36) == longer && joined.IndexOf("9a") == 35 && joined.Replace("xyz", "-") == "abcdefghijklmnopqrstuvw-0123456789abcdefghijklmnopqrstuvw-0123456789" ? 10 : 0;
                u8[] units = longer.Units();
                units[0] = 90;
                u8[] target = new u8[40];
                longer.CopyTo(30, target, 2, 6);
                r += longer[0] == 97 && target[2] == 52 && target[7] == 57 && new String<u8>(units, 1, 3) == "bcd" && new String<u8>(units) != longer ? 100 : 0;
                StringBuilder sb = new StringBuilder();
                loop (i32 i = 0..300)
                    sb.Append(i.ToString()).Append(",");
                str built = sb.ToString();
                r += built.Length == 1090 && built.StartsWith("0,1,2") && built.EndsWith("299,") && (-1234567).ToString() == "-1234567" && Int64.MinValue.ToString() == "-9223372036854775808" ? 1000 : 0;
                return r;
            }

            public PrimitiveLimits() : i32
            {
                i32 r = 0;
                // Constants: usable in other constants, every integer type's limits, every float type's special values.
                r += Widest == 2147483648 && Byte == 255 && Int8.MinValue == -128 && UInt16.MaxValue == 65535 && UInt32.MaxValue == 4294967295
                    && UInt64.MinValue == 0 && Int64.MinValue == -9223372036854775807 - 1 ? 1 : 0;
                r += Float64.IsNaN(Float64.NaN) && Float64.PositiveInfinity > Float64.MaxValue && Float64.NegativeInfinity < Float64.MinValue
                    && Float64.Epsilon > 0.0 && Float64.Epsilon / 2.0 == 0.0 && Float32.Bits(Float32.MaxValue) == 2139095039 && Float32.Bits(Float32.Epsilon) == 1 ? 10 : 0;
                r += Float16.Bits(Float16.NaN) == 32256 && Float16.Bits(Float16.Epsilon) == 1 && Float128.Bits(Float128.MaxValue) == 170135991163610696904058773219554885631u128
                    && Float256.Bits(Float256.Epsilon) == 1 && Float128.IsNaN(Float128.NaN) ? 100 : 0;
                return r;
            }

            public DeclaredKinds() : i32
            {
                i32 r = 0;
                try { throw new MissingException("x"); }
                catch (MissingException e) { r += e.Message == "x" ? 1 : 0; }
                try { Rethrow(new GoneException()); }
                catch (MissingException e) { r += e.Message == "gone" ? 10 : 0; }
                r += Inner.Placed.Value() + Inner.Doubled(2) == 7 && Notes(new NoteAttribute("n")) == 1 ? 100 : 0;
                return r;
            }

            public BoxedValues() : i32
            {
                i32 r = 0;
                func<i32> : i32 twice = (i32 x) => x * 2;
                any held = twice;
                func<i32> : i32 back = (func<i32> : i32)held;
                r += back(21) == 42 && held.ToString() == "func<i32> : i32" ? 1 : 0;
                unsafe
                {
                    i32 v = 5;
                    i32* p = &v;
                    any boxed = p;
                    i32* q = (i32*)boxed;
                    r += *q == 5 && boxed.ToString() == "i32*" ? 10 : 0;
                }
                return r;
            }

            public Tuples() : i32
            {
                i32 r = 0;
                (i32, str) p = (1, "uno");
                r += p.Item1 == 1 && p.Item2 == "uno" && p.ToString() == "(1, uno)" && p == (1, "uno") && p != (2, "uno") ? 1 : 0;
                Dictionary<(i32, i32), i32> grid = new Dictionary<(i32, i32), i32>();
                grid[(1, 2)] = 5;
                grid[(3, 4)] = 6;
                r += grid[(1, 2)] + grid[(3, 4)] == 11 && grid.ContainsKey((1, 2)) && !grid.ContainsKey((2, 1)) ? 10 : 0;
                Tuple<i32, str> boxed = p.ToTuple();
                (i64, f64) wide = (3, 4);
                bool matched = false;
                if (p is (1, var text))
                    matched = text == "uno";
                r += boxed.Item2 == "uno" && boxed.ToValueTuple() == p && boxed.ToString() == "(1, uno)" && wide.Item1 == 3 && matched ? 100 : 0;
                (i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, str) big = (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, "x");
                r += big.Item17 == "x" && big.Item16 == 16 && big.ToString() == "(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, x)" && big == big
                    && big.ToTuple().Item17 == "x" ? 1000 : 0;
                (i32, i32) s = (1, 2);
                s.Item1 = 5;
                (str?, i32) none = (null, 2);
                ((i32, i32), bool) nested = ((1, 2), true);
                r += (s with { Item2 = 9 }) == (5, 9) && none.ToString() == "(null, 2)" && none.Equals((null, 2)) && nested.Item1.Item2 == 2
                    && nested.ToString() == "((1, 2), true)" ? 10000 : 0;
                return r;
            }

            static TypeName<T>() : str => nameof(T);

            public NameOfTypes() : i32
            {
                i32 r = 0;
                r += nameof(i32) == "Int32" && nameof(List<i32>) == "List" && nameof(str[]) == "String[]" && nameof(i32?) == "Int32?"
                    && nameof(any) == "Any" && nameof(str) == "String" && nameof((i32, str)) == "ValueTuple" ? 1 : 0;
                // A type parameter: the type it is given, not its own name.
                r += TypeName<f64>() == "Float64" && TypeName<List<str>>() == "List" && TypeName<Point2>() == "Point2" && TypeName<u8[]>() == "UInt8[]" ? 10 : 0;
                return r;
            }

            public NameOfAndAny() : i32
            {
                i32 r = 0;
                i32 count = 3;
                List<i32> items = new List<i32>();
                r += nameof(count) == "count" && nameof(items.Count) == "Count" && nameof(List) == "List" && nameof(System.Console) == "Console" && nameof(Math.Sqrt) == "Sqrt" ? 1 : 0;
                // `any` is System.Any: the root of every class, and what a boxed value is.
                any boxed = items;
                any number = (any)42;
                System.Any same = boxed;
                r += boxed is List<i32> && number is i32 && same == boxed && new System.Any().ToString() == "System.Any" && typeof(any) == typeof(System.Any) ? 10 : 0;
                return r;
            }

            public MathWide() : i32
            {
                i32 r = 0;
                // f128/f256: computed in the type, not through f64 (which would stop at about 16 digits).
                r += Math.Sqrt((f128)2).ToString() == "1.4142135623730950488016887242096982" && Math.Floor((f128)1 / (f128)3 + (f128)12345678901234567890) == (f128)12345678901234567890
                    && Math.Pow((f128)3, (f128)40) == (f128)12157665459056928801 && Math.Ceiling((f128)(-2.5)) == (f128)(-2) && Math.Round((f128)2.5) == (f128)2 && Math.Truncate((f128)(-7.9)) == (f128)(-7) ? 1 : 0;
                r += Math.Exp((f128)1).ToString() == "2.718281828459045235360287471352662" && (Math.Atan((f128)1) * (f128)4).ToString() == "3.141592653589793238462643383279503"
                    && Math.Abs(Math.Sin((f128)1) - (f128)1 / (f128)1000000000 * (f128)841470984.80789650665250232163029900) < (f128)1e-32 ? 10 : 0;
                r += (Math.Atan2((f256)1, (f256)1) * (f256)4).ToString() == "3.1415926535897932384626433832795028841971693993751058209749445923078164"
                    && Math.Sqrt((f256)2).ToString() == "1.41421356237309504880168872420969807856967187537694807317667973799073247" ? 100 : 0;
                // A u256 far from a power of two: Newton's method in the type (the f64 estimate is off by about 10^22).
                u256 big = Math.Pow((u256)3, (u256)150) + (u256)12345;
                r += Math.Sqrt(big) == Math.Pow((u256)3, (u256)75) && Math.Sqrt((u256)0 - (u256)1) == (u256)340282366920938463463374607431768211455 ? 1000 : 0;
                return r;
            }

            /// Every primitive has its own Write/WriteLine (no ambiguity for u8, u16, u32; no boxing for the wide ones).
            static Printed()
            {
                Console.WriteLine((u8)1); Console.WriteLine((u16)1); Console.WriteLine((u32)1); Console.WriteLine((i8)1); Console.WriteLine((i16)1);
                Console.WriteLine((i128)1); Console.WriteLine((u256)1); Console.WriteLine((f16)1); Console.WriteLine((f32)1); Console.WriteLine((f256)1);
                Console.Write((u32)1); Console.Write((i256)1); Console.Write((f128)1);
            }

            public PrimitiveText() : i32
            {
                System.IO.StringWriter w = new System.IO.StringWriter();
                w.Write((u32)4000000000); w.Write(' ');
                w.Write((i8)(-8)); w.Write(' ');
                w.Write((u128)0 - (u128)1); w.Write(' ');
                w.Write((i256)(-7)); w.Write(' ');
                w.Write((f32)0.1); w.Write(' ');
                w.Write((f16)1.5); w.Write(' ');
                w.WriteLine((f128)1 / (f128)3);
                return w.ToString() == "4000000000 -8 340282366920938463463374607431768211455 -7 0.1 1.5 0.3333333333333333333333333333333333\n" && ((f32)0.1).ToString() == "0.1" ? 1 : 0;
            }

            public MathIntegers() : i32
            {
                i32 r = 0;
                r += Math.Log(1000, 10) == 3 && Math.Log(243, 3) == 5 && Math.Log2(8) == 3 && Math.Log10(1000) == 3 && Math.Log2(1) == 0 ? 1 : 0;
                r += Math.Pow((i64)3, (i64)39) == 4052555153018976267 && Math.Pow((i64)7, (i64)22) == 3909821048582988049 && Math.Pow(5, 0) == 1 ? 10 : 0;
                r += Math.Sqrt((i32)2147483647) == 46340 && Math.Sqrt((u64)18446744073709551615) == 4294967295 && (i32)Math.Sqrt((u8)255) == 15 ? 100 : 0;
                r += Math.Abs(Math.Log(8.0, 2.0) - 3.0) < 0.000000001 && Math.Abs(Math.Pow(2.0, 0.5) - 1.4142135623730951) < 0.000000001 ? 1000 : 0;
                return r;
            }

            public MathGeneric() : i32
            {
                i32 r = 0;
                r += Math.Sqrt(17) == 4 && Math.Sqrt((i64)1000000000000) == 1000000 && Math.Sqrt((u128)99999999999999999999999) == (u128)316227766016 ? 1 : 0;
                r += Math.Floor(7) == 7 && Math.Round(9, 2) == 9 && Math.Floor((f32)2.7) == (f32)2.0 && Math.Ceiling((f32)2.1) == (f32)3.0 ? 10 : 0;
                r += Math.Sqrt(new Cash(225)) == new Cash(150) && Math.Floor(new Cash(275)) == new Cash(200) && Math.Abs(new Cash(-150)) == new Cash(150) ? 100 : 0;
                r += Math.Pow(2, 10) == 1024 && Math.Log2(1024) == 10 && Math.Sqrt((f32)2.0) == (f32)1.4142135 ? 1000 : 0;
                r += Math.Abs((f32)-2.5) == (f32)2.5 && Math.Truncate(-2.7) == -2.0 && Math.Round((f32)2.5) == (f32)2.0 && Math.Sin(0) == 0 ? 10000 : 0;
                return r;
            }

            public Doubles() : i32 => (i32)Float64.Parse("3.25e2") + (Float64.IsNaN(Float64.NaN) ? 1000 : 0) + ((5.5).ToString() == "5.5" ? 10000 : 0)
                + (Float64.IsInfinity(Float64.PositiveInfinity) ? 100000 : 0) + (Float64.Bits(1.0) == 4607182418800017408 ? 1000000 : 0);

            public Chars() : i32 => (Char.IsDigit('7') ? 1 : 0) + (Char.IsLetter('x') ? 10 : 0) + (Char.IsWhiteSpace(' ') ? 100 : 0)
                + (Char.ToUpper('q') == 'Q' ? 1000 : 0) + Char.DigitValue('8') * 10000;

            public Sorting() : i32
            {
                i32[] numbers = new i32[1000];
                i32 x = 12345;
                loop (i32 i = 0..numbers.Length)
                {
                    x = x * 1103515245 + 12345;
                    numbers[i] = x >> 8 & 0xFFFFFF;
                }
                Array.Sort(numbers);
                i32 sorted = 1;
                loop (i32 i = 1..numbers.Length)
                {
                    if (numbers[i - 1] > numbers[i])
                        sorted = 0;
                }
                List<str> words = new List<str>();
                words.Add("pear");
                words.Add("apple");
                words.Add("fig");
                words.Add("banana");
                Lists.Sort(words);
                i32 found = Array.BinarySearch(numbers, numbers[500]) == 500 || numbers[Array.BinarySearch(numbers, numbers[500])] == numbers[500] ? 1000 : 0;
                return sorted + (String.Join(",", words) == "apple,banana,fig,pear" ? 100 : 0) + found + (Array.BinarySearch(numbers, -1) < 0 ? 10 : 0);
            }

            public RadixSorts() : i32
            {
                i32 r = 0;
                i32 x = 777;
                // i32 with negatives, against the comparison sort.
                i32[] a = new i32[1200];
                i32[] b = new i32[1200];
                loop (i32 i = 0..a.Length)
                {
                    x = x * 1103515245 + 12345;
                    a[i] = x;
                    b[i] = x;
                }
                Array.Sort(a);
                Array.Sort(b, (p, q) => p < q ? -1 : p > q ? 1 : 0);
                bool same = true;
                loop (i32 i = 0..a.Length)
                    if (a[i] != b[i])
                        same = false;
                r += same && a[0] < 0 && a[1199] > 0 ? 1 : 0;
                // u32 past 2^31: in unsigned order.
                u32[] u = new u32[1100];
                loop (i32 i = 0..u.Length)
                {
                    x = x * 1103515245 + 12345;
                    u[i] = (u32)x;
                }
                Array.Sort(u);
                bool ordered = true;
                loop (i32 i = 1..u.Length)
                    if (u[i - 1] > u[i])
                        ordered = false;
                r += ordered && u[1099] > 2147483648u32 ? 10 : 0;
                // i64 with negatives and large values, against the comparison sort.
                i64[] l = new i64[1200];
                i64[] m = new i64[1200];
                loop (i32 i = 0..l.Length)
                {
                    x = x * 1103515245 + 12345;
                    l[i] = (i64)x * 1000003 - (i64)i * 77;
                    m[i] = l[i];
                }
                Array.Sort(l);
                Array.Sort(m, (p, q) => p < q ? -1 : p > q ? 1 : 0);
                same = true;
                loop (i32 i = 0..l.Length)
                    if (l[i] != m[i])
                        same = false;
                r += same && l[0] < 0 ? 100 : 0;
                // u64 in unsigned order.
                u64[] w = new u64[1100];
                loop (i32 i = 0..w.Length)
                {
                    x = x * 1103515245 + 12345;
                    w[i] = (u64)(i64)x * 6364136223846793005u64;
                }
                Array.Sort(w);
                ordered = true;
                loop (i32 i = 1..w.Length)
                    if (w[i - 1] > w[i])
                        ordered = false;
                r += ordered ? 1000 : 0;
                // A part of an array: only it is sorted.
                i32[] part = new i32[1300];
                loop (i32 i = 0..part.Length)
                    part[i] = 1300 - i;
                Array.Sort(part, 100, 1100);
                ordered = part[0] == 1300 && part[99] == 1201 && part[1200] == 100 && part[100] == 101 && part[1199] == 1200;
                loop (i32 i = 101..1200)
                    if (part[i - 1] > part[i])
                        ordered = false;
                r += ordered ? 10000 : 0;
                // Every item the same, and small values (passes with one byte value are skipped).
                i32[] equal = new i32[1100];
                loop (i32 i = 0..equal.Length)
                    equal[i] = i % 2 == 0 ? 42 : 41;
                Array.Sort(equal);
                r += equal[0] == 41 && equal[549] == 41 && equal[550] == 42 && equal[1099] == 42 ? 100000 : 0;
                return r;
            }

            public Collections2() : i32
            {
                Queue<i32> queue = new Queue<i32>();
                loop (i32 i = 1..=100)
                    queue.Enqueue(i);
                i32 first = 0;
                loop (i32 i = 0..50)
                    first += queue.Dequeue();
                loop (i32 i = 101..=150)
                    queue.Enqueue(i);
                i32 rest = 0;
                loop (!queue.IsEmpty)
                    rest += queue.Dequeue();
                Stack<i32> stack = new Stack<i32>();
                loop (i32 i = 1..=10)
                    stack.Push(i);
                return first + rest + stack.Pop() * 100000 + queue.Count;
            }

            public ListMore() : i32
            {
                List<i32> list = new List<i32>();
                list.Add(1);
                list.Add(2);
                list.Add(3);
                List<i32> more = new List<i32>();
                more.Add(4);
                more.Add(5);
                list.AddRange(more);
                list.Reverse();
                List<i32> range = list.GetRange(1, 3);
                i32 r = list.Count * 100 + range[0] * 10 + range[2] + Lists.IndexOf(list, 3) * 1000 + (Lists.Contains(list, 9) ? 1 : 0);
                return r + (Lists.Remove(list, 4) ? 10000 : 0);
            }

            public Utf8() : i32
            {
                str text = "héllo €𝄞";
                u8[] bytes = Encoding.UTF8.GetBytes(text);
                str back = Encoding.UTF8.GetString(bytes);
                return bytes.Length * 10 + (back == text ? 1 : 0) + bytes[1] * 1000;
            }

            /// Text generic over its code unit: str (UTF-8) next to String<u16> and String<u32>, the same algorithms.
            public GenericText() : i32
            {
                str text = "añ€𝄞";
                String<u16> wide = text.ToUtf16();
                String<u32> points = text.ToUtf32();
                i32 r = text.Length + wide.Length * 10 + points.Length * 100;
                r += wide.ToUtf8() == text && points.ToUtf8() == text ? 1000 : 0;
                r += text.CodePointCount() == 4 && points[3] == 0x1D11E ? 10000 : 0;
                String<u16> abc = "a,b,c".ToUtf16();
                r += abc.Split(',').Count == 3 && abc.ToUpper().ToUtf8() == "A,B,C" ? 100000 : 0;
                StringBuilder sb = new StringBuilder();
                sb.Append('x').Append((u16)233).AppendCodePoint(0x1F600);
                r += sb.ToString() == "xé😀" && sb.Length == 7 ? 1000000 : 0;
                List<str> parts = new List<str>();
                parts.Add("a");
                parts.Add("ü");
                return r + (str.Join("-", parts) == "a-ü" && String.Join("+", parts) == "a+ü" ? 10000000 : 0);
            }

            public interface IFigure
            {
                Area() : f64;
                Name() : str;
            }

            public interface ISolid : IFigure
            {
                Volume(f64 depth) : f64;
            }

            public class Circle : ISolid
            {
                f64 r;
                public:
                    new(f64 r) { this.r = r; }
                    Area() : f64 => 3.0 * r * r;
                    Name() : str => "circle";
                    Volume(f64 depth) : f64 => Area() * depth;
            }

            public class Ring : Circle
            {
                public:
                    new(f64 r) : base(r) { }
                    override ToString() : str => "ring";
            }

            public value Square : IFigure
            {
                f64 side;
                public:
                    new(f64 side) { this.side = side; }
                    Area() : f64 => side * side;
                    Name() : str => "square";
                    ToString() : str => "square " + side;
            }

            /// Interface values: objects of classes and boxed values in one list, calls through them, is/as, downcasts.
            public InterfaceValues() : i32
            {
                List<IFigure> shapes = new List<IFigure>();
                shapes.Add(new Circle(1.0));
                shapes.Add(new Square(2.0));
                shapes.Add(new Ring(2.0));
                f64 area = 0;
                i32 names = 0;
                loop (IFigure s in shapes)
                {
                    area += s.Area();
                    names += s.Name().Length;
                }
                i32 r = (i32)area + names * 100;
                ISolid? solid = shapes[2] as ISolid;
                if (solid is ISolid found)
                    r += (i32)found.Volume(2.0) * 1000;
                r += shapes[1] is ISolid ? 0 : 10000;
                r += shapes[2] is Circle c && c is Ring ? 100000 : 0;
                Circle back = (Circle)shapes[0];
                r += back.Name() == "circle" ? 1000000 : 0;
                r += shapes[2].ToString() == "ring" && shapes[1].ToString() == "square 2" ? 10000000 : 0;
                IComparable<str> text = "b";
                return r + (text.CompareTo("a") == 1 ? 100000000 : 0);
            }

            /// Values as `any`: boxed, unboxed (checked), tested, their ToString and Hash; a value type through its interface.
            public Boxing() : i32
            {
                List<any> things = new List<any>();
                things.Add(42);
                things.Add(2.5);
                things.Add(true);
                things.Add("text");
                things.Add(new Square(3.0));
                things.Add((i64)7);
                i32 r = 0;
                loop (any thing in things)
                {
                    if (thing is i32 n)
                        r += n;
                    else if (thing is f64 x)
                        r += (i32)(x * 10) * 100;
                    else if (thing is bool)
                        r += 10000;
                    else if (thing is str s)
                        r += s.Length * 100000;
                    else if (thing is Square sq)
                        r += (i32)sq.Area() * 1000000;
                }
                r += things[0].ToString() == "42" && things[2].ToString() == "true" && things[4].ToString() == "square 3" ? 100000000 : 0;
                i64 unboxed = (i64)things[5];
                IHashable<i32> hashable = 9;
                return r + (i32)unboxed * 10 + (i32)hashable.Hash() + (things[0].Hash() == 42 ? 1000 : 0);
            }

            public Strings2() : i32 => ("abc".PadLeft(5, '*') == "**abc" ? 1 : 0) + ("ab".Repeat(3) == "ababab" ? 10 : 0)
                + ("a-b--c".Split("--").Count == 2 ? 100 : 0) + ("x\r\ny\nz".Lines().Count == 3 ? 1000 : 0) + "hello".LastIndexOf('l') * 10000
                + ("hello".Insert(5, "!") == "hello!" ? 100000 : 0) + ("hello".Remove(0, 1) == "ello" ? 1000000 : 0);

            public FileIO() : i32
            {
                str? temp = Environment.GetEnvironmentVariable("TEMP");
                str folder = temp is str found ? found : "/tmp";
                str dir = Path.Combine(folder, "larva-core-io");
                Directory.CreateDirectory(dir);
                str file = Path.Combine(dir, "notes.txt");
                File.WriteAllText(file, "one\ntwo\n");
                File.AppendAllText(file, "three €\n");
                List<str> lines = File.ReadAllLines(file);
                i32 r = lines.Count + (lines[2] == "three €" ? 10 : 0) + (File.Exists(file) ? 100 : 0);
                FileStream stream = new FileStream(file, FileMode.Open);
                u8[] buffer = new u8[3];
                i32 n = stream.Read(buffer, 0, 3);
                stream.Close();
                r += n * 1000 + (buffer[0] == 'o' ? 10000 : 0);
                str other = Path.Combine(dir, "w.txt");
                StreamWriter writer = new StreamWriter(other);
                writer.WriteLine("a");
                writer.Write("b");
                writer.Close();
                r += File.ReadAllText(other).Length * 100000;
                File.Delete(file);
                File.Delete(other);
                return r + (File.Exists(file) ? 0 : 1000000);
            }

            public class Tally : Stream
            {
                i32 bytes;
                public:
                    i32 Bytes { get => bytes; }
                    override bool CanWrite { get => true; }
                    override Write(u8[] buffer, i32 offset, i32 count) { bytes += count; }
            }

            public Streams() : i32
            {
                MemoryStream memory = new MemoryStream();
                BinaryWriter w = new BinaryWriter(memory);
                w.Write(1000000007);
                w.Write((i64)-5);
                w.Write(2.5);
                w.Write((f32)0.75);
                w.Write("año €");
                w.Write((u16)513);
                memory.Position = 0;
                BinaryReader rd = new BinaryReader(memory);
                i32 r = rd.ReadInt32() == 1000000007 ? 1 : 0;
                r += rd.ReadInt64() == -5 ? 10 : 0;
                r += rd.ReadFloat64() == 2.5 && rd.ReadFloat32() == (f32)0.75 ? 100 : 0;
                r += rd.ReadString() == "año €" && rd.ReadUInt16() == 513 ? 1000 : 0;
                bool ended = false;
                try { rd.ReadByte(); } catch (EndOfStreamError e) { ended = true; }
                r += ended ? 10000 : 0;
                StreamReader lines = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("uno\r\ndos\n\ntres")), 16);
                i32 count = 0;
                str joined = "";
                str? line = lines.ReadLine();
                loop (line is str text)
                {
                    count++;
                    joined = joined + text + "|";
                    line = lines.ReadLine();
                }
                r += count == 4 && joined == "uno|dos||tres|" ? 100000 : 0;
                Tally tally = new Tally();
                TextWriter tw = new StreamWriter(new BufferedStream(tally, 8));
                tw.Write("12345");
                tw.Write('6');
                tw.WriteLine(7);
                tw.Flush();
                r += tally.Bytes == 8 ? 1000000 : 0;
                StringReader sr = new StringReader("é\nz");
                r += sr.Read() == 233 && sr.Peek() == 10 && (sr.ReadLine() ?? "x") == "" && sr.ReadToEnd() == "z" ? 10000000 : 0;
                StringWriter sw = new StringWriter();
                sw.Write(true);
                sw.Write(-3);
                return r + (sw.ToString() == "true-3" ? 100000000 : 0);
            }

            public Paths() : i32 => (Path.GetFileName("a/b/c.txt") == "c.txt" ? 1 : 0) + (Path.GetExtension("a/b/c.txt") == ".txt" ? 10 : 0)
                + (Path.GetFileNameWithoutExtension("a/b/c.txt") == "c" ? 100 : 0) + (Path.GetDirectoryName("a/b/c.txt") == "a/b" ? 1000 : 0)
                + (Path.ChangeExtension("a/b/c.txt", "md") == "a/b/c.md" ? 10000 : 0) + (Path.Combine("a", "b") == "a/b" ? 100000 : 0);

            public Environs() : i32
            {
                Stopwatch watch = Stopwatch.StartNew();
                Thread.Sleep(20);
                return (watch.ElapsedMilliseconds >= 15 ? 1 : 0) + (Environment.TickCount64 > 0 ? 10 : 0)
                    + (Environment.GetCommandLineArgs().Count >= 0 ? 100 : 0) + (GC.ObjectCount() > 0 ? 1000 : 0);
            }

            public class Accum
            {
                i32 total;
                public:
                    Adder() : func<i32> : i32 => x => { total += x; return total; };
            }

            public Compose(func<i32> : i32 f, func<i32> : i32 g) : func<i32> : i32 => x => g(f(x));

            Twice(i32 x) : i32 => x * 2;

            public Lambdas() : i32
            {
                i32 r = 0;
                func<i32> : i32 square = x => x * x;
                r += square(3);
                i32 offset = 10;
                func<i32> : i32 shift = x => x + offset;
                offset = 20;
                r += shift(1);
                func<i32> : i32 both = Compose(square, shift);
                r += both(2) * 100;
                Accum a = new Accum();
                func<i32> : i32 add = a.Adder();
                add(5);
                r += add(6) * 10000;
                i32 calls = 0;
                func bump = () => { calls++; };
                bump();
                bump();
                r += calls * 1000000;
                func<i32> : i32 twice = Twice;
                r += twice(4) * 10000000;
                func<i32> : func<i32> : i32 adder = n => m => n + m;
                return r + adder(3)(4) * 100000000;
            }

            public Functional() : i32
            {
                List<i32> xs = new List<i32>();
                loop (i32 i = 1..=10)
                    xs.Add(i);
                List<i32> evens = xs.Where(x => x % 2 == 0);
                List<str> texts = evens.Select(x => "#" + x);
                i32 r = evens.Count + (texts[4] == "#10" ? 10 : 0);
                r += xs.Any(x => x > 9) ? 100 : 0;
                r += xs.All(x => x > 0) ? 1000 : 0;
                r += xs.CountWhere(x => x > 7) * 10000;
                r += xs.Aggregate(0, (sum, x) => sum + x) * 100000;
                i32 limit = 4;
                r += xs.First(x => x > limit) * 10000000;
                xs.RemoveAll(x => x < 4);
                return r + xs.Count * 100000000;
            }

            public SortBy() : i32
            {
                List<str> words = new List<str>();
                words.Add("pear");
                words.Add("fig");
                words.Add("banana");
                words.Add("kiwi");
                words.Sort((a, b) => a.Length - b.Length);
                i32 r = (words[0] == "fig" ? 1 : 0) + (words[3] == "banana" ? 10 : 0);
                i32[] nums = [5, 3, 9, 1, 7];
                Array.Sort(nums, (a, b) => b - a);
                r += nums[0] == 9 && nums[4] == 1 ? 100 : 0;
                i32[] many = new i32[1000];
                loop (i32 i = 0..1000)
                    many[i] = i * 7919 % 1000;
                Array.Sort(many, (a, b) => a < b ? 1 : a > b ? -1 : 0);
                bool sorted = true;
                loop (i32 i = 1..1000)
                    if (many[i - 1] < many[i])
                        sorted = false;
                r += sorted ? 1000 : 0;
                i32 total = 0;
                Array.ForEach(nums, x => { total += x; });
                r += total * 10000;
                return r + Array.FindIndex(nums, x => x == 3) * 1000000;
            }

            public Nullables() : i32
            {
                i32? a = Int32.TryParse("123");
                i32? b = Int32.TryParse("12x");
                i32 r = a ?? 0;
                r += b == null ? 1000 : 0;
                r += b.HasValue ? 0 : 10000;
                f64? d = Float64.TryParse("2.5");
                r += d != null ? (i32)(d.Value * 2) * 100000 : 0;
                i64? big = 7;
                if (big is i64 v)
                    r += (i32)v * 1000000;
                i32? none = null;
                return r + (none ?? 2) * 100000000;
            }

            public class Registry
            {
                public:
                    static List<str> Names = Fill();
                    static i32 Count;
                    static Fill() : List<str> { List<str> l = new List<str>(); l.Add("a" + Later.Suffix); l.Add("b"); return l; }
                    static new() { Count = Names.Count * 10; }

                    public class Entry
                    {
                        public:
                            i32 Id;
                            new(i32 id) { Id = id; }
                    }
                    enum Kind { First, Second }
            }

            public class Later
            {
                public:
                    static str Suffix = "z" + 1;
            }

            public class Chain2<T>
            {
                public:
                    class Link
                    {
                        public:
                            T Value;
                            Link? Next;
                    }
                    Link? head;
                    Push(T v) { Link l = new Link(); l.Value = v; l.Next = head; head = l; }
                    First() : T? => head?.Value;
            }

            public Statics() : i32
            {
                Registry.Entry e = new Registry.Entry(7);
                Chain2<i32> chain = new Chain2<i32>();
                chain.Push(4);
                chain.Push(5);
                i32 r = Registry.Names[0] == "az1" ? 1 : 0;
                r += Registry.Count == 20 ? 10 : 0;
                r += e.Id * 100;
                r += (i32)Registry.Kind.Second * 1000;
                r += (chain.First() ?? 0) * 10000;
                Registry.Count++;
                return r + (Registry.Count == 21 ? 100000 : 0);
            }

            public class Cell
            {
                public:
                    Cell? Next;
                    i32 V;
                    str Name = "c";
                    Twice() : i32 => V * 2;
            }

            public Conditional() : i32
            {
                Cell c = new Cell();
                c.V = 4;
                Cell? none = null;
                Cell? some = c;
                i32 r = some?.V ?? 0;
                r += (none?.V ?? 3) * 10;
                r += (some?.Next?.V ?? 5) * 100;
                r += (some?.Twice() ?? 0) * 1000;
                str? text = null;
                r += (text?.Length ?? 6) * 100000;
                return r + ((some?.Name ?? "x") == "c" ? 1000000 : 0);
            }

            public Captures() : i32
            {
                List<func : i32> fs = new List<func : i32>();
                i32 shared = 0;
                loop (i32 i = 0..3)
                    fs.Add(() => i);
                List<i32> items = new List<i32>();
                items.Add(7);
                items.Add(8);
                loop (i32 x in items)
                    fs.Add(() => x + shared);
                i32 n = 0;
                loop (n < 2)
                {
                    i32 copy = n + 1;
                    fs.Add(() => { shared++; return copy; });
                    n++;
                }
                shared = 1;
                i32 r = 0;
                loop (func : i32 f in fs)
                    r = r * 10 + f() % 10;
                return r + shared * 0;
            }

            public Grids() : i32
            {
                i32[,] g = new i32[3, 4];
                g[2, 3] = 5;
                g[0, 1] = 2;
                i32[,] sq = [[1, 2], [3, 4]];
                i32 order = 0;
                loop (i32 x in sq)
                    order = order * 10 + x;
                f64[,,] cube = new f64[2, 3, 4];
                cube[1, 2, 3] = 0.5;
                i32 d = 2;
                i32 r = g[2, 3] + g[0, 1] * 10;
                r += order * 100;
                r += cube.Length == 24 && cube.Rank == 3 && cube.GetLength(1) == 3 && cube.GetLength(d) == 4 ? 1000000 : 0;
                return r + (cube[1, 2, 3] == 0.5 ? 10000000 : 0);
            }

            public Numbers2(i32 from, i32 to) : Iterator<i32>
            {
                loop (i32 i = from..to)
                    yield i;
            }

            public EvensOf(Iterator<i32> source) : Iterator<i32>
            {
                loop (i32 x in source)
                {
                    if (x % 2 == 0)
                        yield x;
                    if (x >= 6)
                        yield break;
                }
            }

            public Twice<T>(List<T> items) : Iterator<T>
            {
                loop (T item in items)
                {
                    yield item;
                    yield item;
                }
            }

            public Iterators() : i32
            {
                i32 r = 0;
                loop (i32 x in EvensOf(Numbers2(1, 100)))
                    r = r * 10 + x;
                List<str> letters = new List<str>();
                letters.Add("a");
                letters.Add("b");
                str joined = "";
                loop (str s in Twice(letters))
                    joined = joined + s;
                Iterator<i32> it = Numbers2(5, 6);
                bool first = it.Next();
                i32 current = it.Current;
                bool more = it.Next();
                r += joined == "aabb" ? 1000 : 0;
                return r + (first && current == 5 && !more && !it.Next() ? 10000 : 0);
            }

            public async Fetch2(i32 n) : Task<i32>
            {
                await Task.Yield();
                return n * 2;
            }

            public async Broken() : Task<i32>
            {
                await Task.Yield();
                throw new InvalidOperationError("no");
            }

            public async Rescue() : Task<i32>
            {
                try
                {
                    return await Broken();
                }
                catch (InvalidOperationError e)
                {
                    return 7;
                }
            }

            public class Ticker
            {
                public:
                    str Log = "";
                    async Tick(str name, i32 times) : Task
                    {
                        loop (i32 i = 0..times)
                        {
                            Log = Log + name;
                            await Task.Yield();
                        }
                    }
            }

            public async Sum2() : Task<i32>
            {
                i32 a = await Fetch2(1);
                await Task.Delay(1);
                i32 b = await Fetch2(20);
                return a + b;
            }

            public Asyncs() : i32
            {
                i32 r = Sum2().Result;
                r += Rescue().Result * 100;
                Ticker t = new Ticker();
                Task[] both = [t.Tick("a", 2), t.Tick("b", 2)];
                Task.WhenAll(both).Wait();
                r += t.Log == "abab" ? 1000 : 0;
                r += Task<i32>.FromResult(9).Result * 10000;
                return r;
            }

            public HalfOf<T : float>(T x) : T => x * 0.5;

            public CountBits<T : integer>(T x) : i32
            {
                i32 n = 0;
                loop (x != 0)
                {
                    n += (i32)(x & 1);
                    x = x >> 1;
                }
                return n;
            }

            public GenericMath() : i32
            {
                i32[] a = [5, 1, 9];
                f64[] f = [1.5, 2.5];
                u8[] b = [200, 100];
                i32 r = Array.Sum(a) == 15 ? 1 : 0;
                r += Array.Max(f) == 2.5 && Array.Min(a) == 1 ? 10 : 0;
                r += Array.Sum(b) == 44 ? 100 : 0;
                r += Math.Max((u8)3, (u8)9) == 9 && Math.Abs((i16)-4) == 4 ? 1000 : 0;
                r += Math.Clamp((f32)7, (f32)0, (f32)5) == 5 ? 10000 : 0;
                List<i64> l = new List<i64>();
                l.Add(3);
                l.Add(4);
                r += Lists.Sum(l) == 7 && Lists.Average(l) == 3.5 ? 100000 : 0;
                return r + (HalfOf(3.0) == 1.5 && CountBits((u64)255) == 8 ? 1000000 : 0);
            }

            public Checks() : i32
            {
                i32 big = 2147483647;
                i32 r = 0;
                try { i32 v = checked(big + 1); } catch (OverflowError e) { r += 1; }
                try { i64 v = checked((i64)9223372036854775807 * 2); } catch (OverflowError e) { r += 10; }
                i32 wide = 300;
                try { u8 v = checked((u8)wide); } catch (OverflowError e) { r += 100; }
                try { checked { big++; } } catch (OverflowError e) { r += 1000; }
                i32 smallest = -2147483647 - 1;
                try { i32 v = checked(-smallest); } catch (OverflowError e) { r += 10000; }
                i32 w = unchecked(big + 1);
                r += w == smallest ? 100000 : 0;
                i32 fits = checked(46340 * 46340);
                return r + (fits == 2147395600 ? 1000000 : 0);
            }

            public Randoms() : i32
            {
                Random a = new Random(7);
                Random b = new Random(7);
                bool same = true;
                loop (i32 i = 0..100)
                {
                    if (a.Next() != b.Next())
                        same = false;
                }
                i32 r = same ? 1 : 0;
                bool inRange = true;
                loop (i32 i = 0..1000)
                {
                    i32 v = a.Next(-3, 4);
                    f64 d = a.NextDouble();
                    i64 big = a.NextInt64(5000000000);
                    if (v < -3 || v >= 4 || d < 0 || d >= 1 || big < 0 || big >= 5000000000)
                        inRange = false;
                }
                r += inRange ? 10 : 0;
                i32[] items = [1, 2, 3, 4, 5, 6];
                a.Shuffle(items);
                i32 sum = 0;
                loop (i32 x in items)
                    sum += x;
                r += sum == 21 ? 100 : 0;
                u8[] bytes = new u8[13];
                a.NextBytes(bytes);
                i32 nonzero = 0;
                loop (u8 x in bytes)
                {
                    if (x != 0)
                        nonzero++;
                }
                r += nonzero > 5 ? 1000 : 0;
                i32[] counts = new i32[4];
                loop (i32 i = 0..4000)
                    counts[a.Next(4)]++;
                bool even = true;
                loop (i32 c in counts)
                {
                    if (c < 800 || c > 1200)
                        even = false;
                }
                return r + (even ? 10000 : 0);
            }

            public Formats() : i32
            {
                i32 r = (255).ToString("X4") == "00FF" && (-1).ToString("X") == "FFFFFFFF" ? 1 : 0;
                r += ((i64)1234567).ToString("N0") == "1,234,567" ? 10 : 0;
                r += (2.675).ToString("F2") == "2.67" && (0.125).ToString("F2") == "0.12" ? 100 : 0;
                r += (0.1 + 0.2).ToString("R") == "0.30000000000000004" && (1e20).ToString("R") == "1E+20" ? 1000 : 0;
                r += (1234.5678).ToString("E3") == "1.235E+003" && (0.256).ToString("P1") == "25.6 %" ? 10000 : 0;
                f64 price = 1234.5;
                i32 n = 42;
                r += $"total {price:N2} [{n,4}] [{n,-4}] {{x}}" == "total 1,234.50 [  42] [42  ] {x}" ? 100000 : 0;
                return r + (((f32)0.1).ToString("R") == "0.1" && (5).ToString("D3") == "005" ? 1000000 : 0);
            }

            public Linked() : i32
            {
                LinkedList<str> ll = new LinkedList<str>();
                LinkedListNode<str> b = ll.AddLast("b");
                ll.AddFirst("a");
                ll.AddLast("d");
                ll.AddAfter(b, "c");
                str all = "";
                loop (str x in ll)
                    all = all + x;
                i32 r = all == "abcd" ? 1 : 0;
                ll.Remove(b);
                r += ll.RemoveFirst() == "a" && ll.RemoveLast() == "d" && ll.Count == 1 ? 10 : 0;
                r += ll.Find(x => x == "c") is LinkedListNode<str> found && found.List == ll ? 100 : 0;
                LinkedList<str> other = new LinkedList<str>();
                return r + (other != ll && b.List == null ? 1000 : 0);
            }

            public SortedMaps() : i32
            {
                SortedDictionary<i32, str> map = new SortedDictionary<i32, str>();
                loop (i32 i = 0..20)
                    map[(i * 7) % 20] = "v" + i;
                map.Remove(5);
                i32 r = map.Remove(100) ? 0 : 1;
                i32 last = -1;
                bool ordered = true;
                loop (KeyValuePair<i32, str> p in map)
                {
                    if (p.Key <= last)
                        ordered = false;
                    last = p.Key;
                }
                r += ordered && map.Count == 19 ? 10 : 0;
                r += map[14] == "v2" && map.MinKey() == 0 && map.MaxKey() == 19 && map.TryGet(5, "none") == "none" ? 100 : 0;
                SortedSet<str> set = new SortedSet<str>();
                set.Add("pera");
                set.Add("ajo");
                set.Add("zeta");
                r += set.Add("ajo") ? 0 : 1000;
                str words = "";
                loop (str w in set)
                    words = words + w + " ";
                return r + (words == "ajo pera zeta " && set.Min == "ajo" && set.Max == "zeta" ? 10000 : 0);
            }

            public Priorities() : i32
            {
                PriorityQueue<str, i32> queue = new PriorityQueue<str, i32>();
                queue.Enqueue("c", 3);
                queue.Enqueue("a", 1);
                queue.Enqueue("e", 5);
                queue.Enqueue("b", 2);
                queue.Enqueue("d", 4);
                i32 r = queue.Peek() == "a" && queue.PeekPriority == 1 ? 1 : 0;
                str order = "";
                loop (queue.Count > 0)
                    order = order + queue.Dequeue();
                r += order == "abcde" ? 10 : 0;
                r += queue.EnqueueDequeue("z", 0) == "z" && queue.Count == 0 ? 100 : 0;
                Random random = new Random(5);
                loop (i32 i = 0..1000)
                    queue.Enqueue("x", random.Next(100000));
                i32 previous = -1;
                bool sorted = true;
                loop (queue.Count > 0)
                {
                    i32 priority = queue.PeekPriority;
                    queue.Dequeue();
                    if (priority < previous)
                        sorted = false;
                    previous = priority;
                }
                return r + (sorted ? 1000 : 0);
            }

            public Spans2() : i32
            {
                i32[] numbers = [5, 4, 3, 2, 1, 0];
                Span<i32> view = new Span<i32>(numbers, 1, 4);
                view[0] = 40;
                Span<i32> inner = view.Slice(1, 2);
                Spans.Sort(view);
                str text = "";
                loop (i32 x in view)
                    text = text + x;
                i32 r = text == "12340" && numbers[1] == 1 && numbers[0] == 5 ? 1 : 0;
                r += inner.Length == 2 && Spans.Sum(view) == 46 && Spans.IndexOf(view, 40) == 3 ? 10 : 0;
                bool caught = false;
                try { i32 bad = view[4]; } catch (IndexError e) { caught = true; }
                r += caught ? 100 : 0;
                List<i32> list = new List<i32>();
                list.Add(7);
                list.Add(8);
                Span<i32> ofList = Spans.Of(list);
                ofList[1] = 9;
                return r + (list[1] == 9 && ofList.ToArray().Length == 2 ? 1000 : 0);
            }

            public value Money
            {
                public:
                    i64 Cents;
                    new(i64 cents) { Cents = cents; }
                    op +(Money a, Money b) : Money => new Money(a.Cents + b.Cents);
                    op -(Money a, Money b) : Money => new Money(a.Cents - b.Cents);
                    op -(Money a) : Money => new Money(-a.Cents);
                    op *(Money a, i64 k) : Money => new Money(a.Cents * k);
                    op ==(Money a, Money b) : bool => a.Cents == b.Cents;
                    op <(Money a, Money b) : bool => a.Cents < b.Cents;
                    op !(Money a) : bool => a.Cents == 0;
            }

            public enum Shade { Light, Dark = 5, Deep }

            public Operators() : i32
            {
                Money a = new Money(250), b = new Money(100);
                i32 r = (a + b).Cents == 350 ? 1 : 0;
                r += (a - b * 3).Cents == -50 && (-a).Cents == -250 ? 10 : 0;
                Money c = a;
                c += b;
                c -= new Money(50);
                r += c == new Money(300) && c != a ? 100 : 0;
                r += b < a && !(a < b) && !new Money(0) && !(!a) ? 1000 : 0;
                Shade s = Shade.Deep;
                r += s.ToString() == "Deep" && Shade.Light.ToString() == "Light" && ((Shade)7).ToString() == "7" ? 10000 : 0;
                return r;
            }

            public Dates() : i32
            {
                i32 r = new DateTime(2024, 2, 29).DayOfWeek == DayOfWeek.Thursday && new DateTime(2024, 2, 29).DayOfWeek.ToString() == "Thursday" ? 1 : 0;
                r += new DateTime(2024, 1, 31).AddMonths(1) == new DateTime(2024, 2, 29) && new DateTime(2024, 2, 29).AddYears(1) == new DateTime(2025, 2, 28) ? 10 : 0;
                r += (new DateTime(2025, 3, 1) - new DateTime(2024, 3, 1)).Days == 365 && new DateTime(2024, 3, 1) < new DateTime(2025, 3, 1) ? 100 : 0;
                r += new DateTime(2024, 12, 31).DayOfYear == 366 && DateTime.DaysInMonth(1900, 2) == 28 && DateTime.IsLeapYear(2000) ? 1000 : 0;
                r += DateTime.FromUnixTimeSeconds(1714521600) == new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc) && new DateTime(1970, 1, 2, 0, 0, 0, DateTimeKind.Utc).ToUnixTimeSeconds() == 86400 ? 10000 : 0;
                DateTime d = new DateTime(2024, 5, 1, 13, 45, 30, 123) + TimeSpan.FromDays(1);
                r += d.Day == 2 && d.Hour == 13 && d.Minute == 45 && d.Second == 30 && d.Millisecond == 123 && d.TimeOfDay == new TimeSpan(0, 13, 45, 30, 123) ? 100000 : 0;
                try { DateTime bad = new DateTime(2023, 2, 29); } catch (ArgumentOutOfRangeError e) { r += 1000000; }
                return r;
            }

            public TimeSpans() : i32
            {
                TimeSpan a = TimeSpan.FromHours(1.5);
                i32 r = a.TotalMinutes == 90 && a.Ticks == 54000000000 ? 1 : 0;
                r += (a + TimeSpan.FromMinutes(30)).Hours == 2 && (a - TimeSpan.FromHours(2)).Minutes == -30 ? 10 : 0;
                r += (-a).ToString() == "-01:30:00" && new TimeSpan(1, 2, 3, 4, 5).ToString() == "1.02:03:04.0050000" ? 100 : 0;
                r += TimeSpan.Parse("1.02:03:04.5") == new TimeSpan(1, 2, 3, 4, 500) && TimeSpan.Parse(" -0:30 ") == -TimeSpan.FromMinutes(30) ? 1000 : 0;
                r += TimeSpan.TryParse("25:00") == null && TimeSpan.TryParse("x") == null ? 10000 : 0;
                r += a * 2.0 / TimeSpan.FromMinutes(1) == 180 && (a / 4.0).TotalSeconds == 1350 ? 100000 : 0;
                TimeSpan t = TimeSpan.Zero;
                t += a;
                return r + (t > TimeSpan.FromHours(1) && t.ToString("g") == "1:30:00" && t.ToString("hh\\:mm") == "01:30" ? 1000000 : 0);
            }

            public DateTexts() : i32
            {
                DateTime d = new DateTime(2024, 2, 29, 13, 5, 7, 8);
                i32 r = d.ToString() == "02/29/2024 13:05:07" ? 1 : 0;
                r += d.ToString("o") == "2024-02-29T13:05:07.0080000" && d.ToString("s") == "2024-02-29T13:05:07" ? 10 : 0;
                r += d.ToString("dddd, dd MMMM yyyy h:mm tt") == "Thursday, 29 February 2024 1:05 PM" && d.ToString("R") == "Thu, 29 Feb 2024 13:05:07 GMT" ? 100 : 0;
                r += DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o") == "2024-02-29T13:05:07.0080000Z" ? 1000 : 0;
                r += d.ToString("HH:mm:ss.FFF") == "13:05:07.008" && d.ToString("yy-M-d") == "24-2-29" ? 10000 : 0;
                DateTime? p = DateTime.TryParse("2024-05-01T13:45:30.5Z", DateTimeStyles.RoundtripKind);
                r += p != null && p.Value.Kind == DateTimeKind.Utc && p.Value.Millisecond == 500 && DateTime.Parse("May 1, 2024") == new DateTime(2024, 5, 1) ? 100000 : 0;
                r += DateTime.ParseExact("01/05/24 7:05 PM", "dd/MM/yy h:mm tt") == new DateTime(2024, 5, 1, 19, 5, 0) && DateTime.TryParse("2024-02-30") == null ? 1000000 : 0;
                return r;
            }

            public class Tally2
            {
                public:
                    i32 Count;
            }

            public Threads() : i32
            {
                AtomicI64 total = new AtomicI64();
                Mutex mutex = new Mutex();
                Tally2 tally = new Tally2();
                AtomicI32 otherIds = new AtomicI32();
                List<Thread> threads = new List<Thread>();
                loop (i32 k = 0..4)
                {
                    Thread t = new Thread(() =>
                    {
                        loop (i32 i = 0..2000)
                            total.Increment();
                        loop (i32 i = 0..500)
                        {
                            mutex.Lock();
                            tally.Count++;
                            mutex.Unlock();
                        }
                        if (Thread.CurrentId != 1)
                            otherIds.Increment();
                    });
                    threads.Add(t);
                    t.Start();
                }
                loop (Thread t in threads)
                    t.Join();
                i32 r = total.Load() == 8000 ? 1 : 0;
                r += tally.Count == 2000 ? 10 : 0;
                r += otherIds.Load() == 4 && Thread.CurrentId == 1 ? 100 : 0;
                bool joined = threads[0].Join(10) && !threads[0].IsAlive && threads[0].ManagedThreadId > 1;
                AtomicI64 swap = new AtomicI64(5);
                bool atomics = swap.CompareExchange(9, 5) == 5 && swap.Load() == 9 && swap.CompareExchange(1, 5) == 9 && swap.Exchange(3) == 9 && swap.Add(4) == 7;
                return r + (joined ? 1000 : 0) + (atomics ? 10000 : 0);
            }

            public Syncs() : i32
            {
                SemaphoreSlim gate = new SemaphoreSlim(2);
                AtomicI32 inside = new AtomicI32();
                AtomicI32 most = new AtomicI32();
                CountdownEvent done = new CountdownEvent(6);
                loop (i32 k = 0..6)
                {
                    new Thread(() =>
                    {
                        gate.Wait();
                        i32 now = inside.Increment();
                        loop (true)
                        {
                            i32 seen = most.Load();
                            if (now <= seen || most.CompareExchange(now, seen) == seen)
                                break;
                        }
                        Thread.Sleep(2);
                        inside.Decrement();
                        gate.Release();
                        done.Signal();
                    }).Start();
                }
                done.Wait();
                i32 r = most.Load() <= 2 && most.Load() >= 1 && done.IsSet ? 1 : 0;
                ManualResetEvent go = new ManualResetEvent(false);
                AtomicI32 started = new AtomicI32();
                Thread waiter = new Thread(() => { go.WaitOne(); started.Increment(); });
                waiter.Start();
                Thread.Sleep(5);
                r += started.Load() == 0 ? 10 : 0;
                go.Set();
                waiter.Join();
                r += started.Load() == 1 && go.WaitOne(0) ? 100 : 0;
                AutoResetEvent once = new AutoResetEvent(true);
                r += once.WaitOne(0) && !once.WaitOne(0) ? 1000 : 0;
                BlockingCollection<i32> queue = new BlockingCollection<i32>();
                Thread producer = new Thread(() => { loop (i32 i = 1..201) queue.Add(i); queue.CompleteAdding(); });
                producer.Start();
                i64 sum = 0;
                loop (queue.WaitForItem())
                    sum += queue.Take();
                producer.Join();
                r += sum == 20100 && queue.IsCompleted ? 10000 : 0;
                Mutex m = new Mutex();
                Condition c = new Condition();
                m.Lock();
                bool timedOut = !c.Wait(m, 5);
                m.Unlock();
                return r + (timedOut && m.TryLock() ? 100000 : 0);
            }

            public Parallels() : i32
            {
                AtomicI64 squares = new AtomicI64();
                Parallel.For(0, 1000, i => { squares.Add((i64)i * i); });
                i32 r = squares.Load() == 332833500 ? 1 : 0;
                i64[] slots = new i64[4];
                Parallel.For(0, 4000, i =>
                {
                    unsafe { Interlocked.Increment(&slots[i % 4]); }
                });
                r += slots[0] == 1000 && slots[3] == 1000 ? 10 : 0;
                AtomicI32 calls = new AtomicI32();
                func[] actions = [() => { calls.Add(1); }, () => { calls.Add(10); }, () => { calls.Add(100); }];
                Parallel.Invoke(actions);
                r += calls.Load() == 111 ? 100 : 0;
                ConcurrentQueue<str> words = new ConcurrentQueue<str>();
                Parallel.For(0, 50, i => { words.Enqueue(i.ToString()); });
                i32 taken = 0;
                loop (words.TryDequeue("") != "")
                    taken++;
                r += taken == 50 && words.IsEmpty ? 1000 : 0;
                bool failed = false;
                try { Parallel.For(0, 100, i => { if (i == 37) throw new InvalidOperationError("thirty-seven"); }); }
                catch (InvalidOperationError e) { failed = e.Message == "thirty-seven"; }
                return r + (failed ? 10000 : 0);
            }

            public async Doubled(Task<i32> work) : Task<i32>
            {
                i32 value = await work;
                return value * 2;
            }

            public TaskRuns() : i32
            {
                Task<i32> sum = Task.Run<i32>(() => { i32 s = 0; loop (i32 i = 0..100) s += i; return s; });
                i32 r = sum.Result == 4950 ? 1 : 0;
                r += Doubled(Task.Run<i32>(() => 21)).Result == 42 ? 10 : 0;
                List<Task> many = new List<Task>();
                AtomicI32 count = new AtomicI32();
                loop (i32 i = 0..10)
                    many.Add(Task.Run(() => { count.Increment(); }));
                Task.WhenAll(many.ToArray()).Wait();
                r += count.Load() == 10 ? 100 : 0;
                Task<i32> nested = Task.Run<i32>(() => Task.Run<i32>(() => 7).Result + 1);
                r += nested.Result == 8 ? 1000 : 0;
                Task failing = Task.Run(() => { throw new InvalidOperationError("no"); });
                try { failing.Wait(); } catch (InvalidOperationError e) { r += failing.IsFaulted ? 10000 : 0; }
                return r;
            }

            /// A program's own number: the operators and constants INumber and ISignedNumber ask for.
            public value Cash : ISignedNumber<Cash>
            {
                public:
                    i64 cents;
                    new(i64 cents);
                    static Cash Zero { get => new Cash(0); }
                    static Cash One { get => new Cash(100); }
                    static Cash NegativeOne { get => new Cash(-100); }
                    static FromF64(f64 value) : Cash => new Cash((i64)(value * 100));
                    static ToF64(Cash c) : f64 => (f64)c.cents / 100;
                    op +(Cash a, Cash b) : Cash => new Cash(a.cents + b.cents);
                    op -(Cash a, Cash b) : Cash => new Cash(a.cents - b.cents);
                    op *(Cash a, Cash b) : Cash => new Cash(a.cents * b.cents / 100);
                    op /(Cash a, Cash b) : Cash => new Cash(a.cents * 100 / b.cents);
                    op %(Cash a, Cash b) : Cash => new Cash(a.cents % b.cents);
                    op ==(Cash a, Cash b) : bool => a.cents == b.cents;
                    op <(Cash a, Cash b) : bool => a.cents < b.cents;
                    op -(Cash a) : Cash => new Cash(-a.cents);
            }

            NumTotal<T : number>(T[] items) : T
            {
                T total = T.Zero;
                loop (T item in items)
                    total += item;
                return total;
            }
            NumMax<T : number>(T a, T b) : T => a < b ? b : a;
            NumTwice<T : number>(T x) : T => x * (T.One + T.One);
            NumNeg<T : snumber>(T x) : T => -x;
            NumIsZero<T : number>(T x) : bool => x == default(T);

            public class Bag
            {
                public:
                    i32 V;
                    new(i32 v) { V = v; }
                    new() { V = 7; }
            }

            public interface IOutline
            {
                Area() : f64;
                Describe() : str => "area " + Area().ToString("F0");
                Twice() : f64 => Area() * 2.0;
                static Unit() : f64 => 1.0;
            }

            public class Sq2 : IOutline
            {
                public:
                    f64 S;
                    new(f64 s) { S = s; }
                    Area() : f64 => S * S;
            }

            public value Circ2 : IOutline
            {
                public:
                    f64 R;
                    new(f64 r) { R = r; }
                    Area() : f64 => 3.0 * R * R;
                    Describe() : str => "circle";
            }

            DescOf<T : IOutline>(T x) : str => x.Describe();
            DblOf<T : IOutline>(T x) : f64 => x.Twice();

            public DefaultMethods() : i32
            {
                IOutline sh = new Sq2(2.0);
                i32 r = sh.Describe() == "area 4" ? 1 : 0;
                IOutline c = new Circ2(1.0);
                r += c.Describe() == "circle" && c.Twice() == 6.0 ? 10 : 0;
                r += DescOf(new Sq2(3.0)) == "area 9" && DescOf(new Circ2(2.0)) == "circle" && DblOf(new Circ2(1.0)) == 6.0 ? 100 : 0;
                r += IOutline.Unit() == 1.0 ? 1000 : 0;
                r += INumber<i32>.Zero == 0 && INumber<f64>.One == 1.0 && ISignedNumber<i64>.NegativeOne == -1 && ISignedNumber<Cash>.NegativeOne == new Cash(-100) ? 10000 : 0;
                return r;
            }

            public interface IShape3
            {
                f64 Side { base get; }
                f64 Area { get => Side * Side; }
                str Label { get => "shape"; set { } }
                i32 Tag { get => 1; base set; }
                Describe() : str => Label + " " + Area.ToString("F0");
            }

            public interface IFancy3 : IShape3
            {
                Describe() : str => "fancy " + base.Describe();
            }

            public class Plain3 : IShape3
            {
                public:
                    f64 Side { get; set; }
                    i32 Tag { get => 7; set { } }
                    new(f64 s) { Side = s; }
            }

            public class Loud3 : IShape3
            {
                public:
                    f64 Side { get; set; }
                    i32 Tag { get => 2; set { } }
                    new(f64 s) { Side = s; }
                    str Label { get => "LOUD " + base.Label; set { } }
                    Describe() : str => base.Describe() + "!";
            }

            public value Box3 : IFancy3
            {
                public:
                    f64 Side { get; set; }
                    i32 Tag { get => 3; set { } }
                    new(f64 s) { Side = s; }
                    Describe() : str => "box(" + base.Describe() + ")";
            }

            public class Quiet3 : IFancy3
            {
                public:
                    f64 Side { get; set; }
                    i32 Tag { get => 4; set { } }
                    new(f64 s) { Side = s; }
            }

            public interface IPair3<T>
            {
                T First { base get; }
                T Second { base get; }
                T Chosen { get => Pick(true); }
                Pick(bool first) : T => first ? First : Second;
            }

            public value Two3 : IPair3<i32>
            {
                public:
                    i32 First { get; set; }
                    i32 Second { get; set; }
                    new(i32 a, i32 b) { First = a; Second = b; }
                    Pick(bool first) : i32 => base.Pick(first) * 10;
            }

            public base class Animal3
            {
                public:
                    virtual str Name { get => "animal"; }
                    virtual Speak() : str => "...";
            }

            public class Dog3 : Animal3
            {
                public:
                    override str Name { get => "dog " + base.Name; }
                    override Speak() : str => base.Speak() + "woof";
            }

            public DefaultAccessors() : i32
            {
                i32 r = 0;
                IShape3 p = new Plain3(2.0);
                r += p.Area == 4.0 && p.Label == "shape" && p.Describe() == "shape 4" && p.Tag == 7 ? 1 : 0;
                IShape3 l = new Loud3(3.0);
                r += l.Label == "LOUD shape" && l.Describe() == "LOUD shape 9!" ? 10 : 0;
                IFancy3 b = new Box3(1.0);
                r += b.Describe() == "box(fancy shape 1)" ? 100 : 0;
                Animal3 d = new Dog3();
                r += d.Name == "dog animal" && d.Speak() == "...woof" ? 1000 : 0;
                IShape3 q = new Quiet3(2.0);
                IFancy3 qf = new Quiet3(2.0);
                r += q.Describe() == "fancy shape 4" && qf.Describe() == "fancy shape 4" ? 10000 : 0;
                IPair3<i32> two = new Two3(4, 5);
                r += two.Pick(false) == 50 && two.Chosen == 40 ? 100000 : 0;
                return r;
            }

            public value Vec4<T : number>
            {
                public:
                    T X;
                    T Y;
                    new(T x, T y) { X = x; Y = y; }
                    op +(Vec4<T> a, Vec4<T> b) : Vec4<T> => new Vec4<T>(a.X + b.X, a.Y + b.Y);
                    op *<S : (T)S>(Vec4<T> a, S k) : Vec4<T> => new Vec4<T>(a.X * (T)k, a.Y * (T)k);
            }

            public value Counter4
            {
                public:
                    i32 N;
                    new(i32 n) { N = n; }
                    op ++(Counter4 c) : Counter4 => new Counter4(c.N + 1);
                    op --(Counter4 c) : Counter4 => new Counter4(c.N - 1);
            }

            public class Box4<T>
            {
                public:
                    T Item;
                    new(T item) { Item = item; }
                    op (Pair4<U>)<U>(Box4<U> b) => new Pair4<U>(b.Item, b.Item);
                    op (T)(Box4<T> b) => b.Item;
            }

            public value Pair4<T>
            {
                public:
                    T A;
                    T B;
                    new(T a, T b) { A = a; B = b; }
            }

            public value Mix4
            {
                public:
                    i64 V;
                    new(i64 v) { V = v; }
                    op +(Mix4 a, Mix4 b) : Mix4 => new Mix4(a.V + b.V);
                    op +<S : (i64)S>(Mix4 a, S b) : Mix4 => new Mix4(a.V + (i64)b * 1000);
            }

            public GenericOperators() : i32
            {
                i32 r = 0;
                Vec4<i32> v = new Vec4<i32>(1, 2) + new Vec4<i32>(3, 4);
                r += v.X == 4 && v.Y == 6 ? 1 : 0;
                Vec4<f64> w = new Vec4<f64>(1.5, 2.0) * 2;
                r += w.X == 3.0 && w.Y == 4.0 ? 10 : 0;
                Counter4 c = new Counter4(5);
                Counter4 old = c++;
                Counter4 now = ++c;
                c--;
                r += old.N == 5 && now.N == 7 && c.N == 6 ? 100 : 0;
                Box4<i32> b = new Box4<i32>(9);
                Pair4<i32> p = (Pair4<i32>)b;
                i32 item = (i32)b;
                r += p.A == 9 && p.B == 9 && item == 9 ? 1000 : 0;
                Mix4 m = new Mix4(1) + new Mix4(2);
                Mix4 n = new Mix4(1) + 2;
                r += m.V == 3 && n.V == 2001 ? 10000 : 0;
                return r;
            }

            public value Quad5
            {
                public:
                    fixed i32[4] cells;
                    Sum() : i32
                    {
                        i32 s = 0;
                        loop (i32 i = 0..cells.Length)
                            s += cells[i];
                        return s;
                    }
            }

            public value Slot5<T>
            {
                public:
                    fixed u8[sizeof(T)] bytes;
                    Width() : i32 => bytes.Length;
            }

            public value Twice5<T>
            {
                public:
                    fixed u8[2 * sizeof(T)] bytes;
            }

            public FixedFields() : i32
            {
                i32 r = 0;
                Quad5 q = new Quad5();
                q.cells[0] = 1; q.cells[1] = 2; q.cells[2] = 3; q.cells[3] = 4;
                Quad5 copy = q;
                copy.cells[3] = 40;
                r += q.Sum() == 10 && copy.Sum() == 46 && q.cells.Length == 4 ? 1 : 0;
                Slot5<i32> s = new Slot5<i32>();
                s.bytes[3] = 9;
                Slot5<u16> t = new Slot5<u16>();
                Twice5<i64> w = new Twice5<i64>();
                r += s.Width() == 4 && s.bytes[3] == 9 && t.bytes.Length == 2 && w.bytes.Length == 16 ? 10 : 0;
                Bag b = MakeIt(3);
                Bag f = Fresh();
                r += b.V == 3 && f.V == 7 ? 100 : 0;
                return r;
            }

            public bitflag enum Perm6 : u8 { Read, Write, Exec, All = declbits }

            Next6(Perm6 p) : Perm6 => p.With(Perm6.Exec);

            public Bitflags() : i32
            {
                i32 r = 0;
                Perm6 p = Perm6.Read | Perm6.Write;
                r += p.HasFlag(Perm6.Read) && p.HasFlag(Perm6.Read | Perm6.Write) && !p.HasFlag(Perm6.Exec) && !p.HasFlag(Perm6.All) ? 1 : 0;
                r += p.HasAny(Perm6.Exec | Perm6.Write) && !p.HasAny(Perm6.Exec) ? 10 : 0;
                r += p.With(Perm6.Exec) == Perm6.All && Next6(p) == Perm6.All && p.Without(Perm6.Write) == Perm6.Read && p.Toggle(Perm6.Read | Perm6.Exec) == (Perm6.Write | Perm6.Exec) ? 100 : 0;
                r += p.ToString() == "Read | Write" && Perm6.All.ToString() == "All" && Perm6.Exec.ToString() == "Exec" ? 1000 : 0;
                Perm6 none = p.Without(Perm6.All);
                Perm6 odd = (Perm6)9;
                r += none.ToString() == "0" && odd.ToString() == "Read | 8" && ((Perm6)8).ToString() == "8" ? 10000 : 0;
                return r;
            }

            public value Pt7 : IToString
            {
                public:
                    i32 X;
                    i32 Y;
                    new(i32 x, i32 y);
                    ToString() : str => X.ToString() + "," + Y.ToString();
            }

            public base class Shape7
            {
                public:
                    i32 Id;
                    new(i32 id);
            }

            public class Circle7 : Shape7
            {
                public:
                    f64 R;
                    new(i32 id, f64 r) : base(id);
            }

            public Literals() : i32
            {
                i32 r = 0;
                u8 a = 200u8;
                i64 b = 1_000_000_i64;
                u32 c = 0xFF_u32;
                f32 d = 1.5f32;
                f64 e = 3f64;
                i32 f = 1_000_;
                u16 g = 7u16;
                i8 k = -5i8;
                r += a == 200 && b == 1000000 && c == 255 && d == 1.5 && e == 3.0 && f == 1000 && g == 7 && k == -5 ? 1 : 0;
                Pt7 p = new Pt7(1, 2);
                Circle7 cc = new Circle7(3, 1.5);
                r += p.X == 1 && p.Y == 2 && cc.Id == 3 && cc.R == 1.5 ? 10 : 0;
                any o = p;
                r += o is IToString t && t.ToString() == "1,2" && Float64.Parse("2.5") == 2.5 ? 100 : 0;
                return r;
            }

            public bitflag enum Perm7 : u8 { Read, Write, Exec, All = declbits }

            ext(i32 n)
            {
                public:
                    Twice() : i32 => n * 2;
                    Plus(i32 k) : i32 => n + k;
            }

            ext(Pt7 p)
            {
                public:
                    Sum() : i32 => p.X + p.Y;
            }

            static ext(i32)
            {
                public:
                    Answer() : i32 => 42;
            }

            ext<T>(T[] items)
            {
                public:
                    Count() : i32 => items.Length;
            }

            static ext<T : bitflag enum>(T)
            {
                public:
                    None() : T => default(T);
            }

            public Extensions() : i32
            {
                i32 r = 0;
                i32 five = 5;
                r += five.Twice() == 10 && five.Plus(3) == 8 && (7).Twice() == 14 ? 1 : 0;
                r += new Pt7(1, 2).Sum() == 3 && i32.Answer() == 42 ? 10 : 0;
                str[] names = ["a", "b", "c"];
                r += names.Count() == 3 ? 100 : 0;
                Perm7 p = Perm7.Read | Perm7.Write;
                r += p.HasFlag(Perm7.Read) && !p.HasFlag(Perm7.Exec) && p.HasAny(Perm7.Exec | Perm7.Write) && !p.HasAny(Perm7.Exec) ? 1000 : 0;
                r += p.With(Perm7.Exec) == Perm7.All && p.Without(Perm7.Write) == Perm7.Read && p.Toggle(Perm7.Read | Perm7.Exec) == (Perm7.Write | Perm7.Exec) ? 10000 : 0;
                r += Perm7.None() == p.Without(Perm7.All) ? 100000 : 0;
                return r;
            }

            Big8(i128 a, i128 b) : i128 => a * b + 1i128;
            Sum8<T : snumber>(T a, T b) : T => a + b;

            public WideIntegers() : i32
            {
                i32 r = 0;
                i128 a = 1i128 << 100;
                i128 b = a + a;
                r += b == 1i128 << 101 && b > a && (b / a) == 2i128 && (b % 3i128) == 2i128 ? 1 : 0;
                u128 u = 340282366920938463463374607431768211455u128;
                r += u + 1u128 == 0u128 && (u >> 64) == 18446744073709551615u128 ? 10 : 0;
                i64 small = (i64)(a >> 90);
                i128 widened = (i128)small * 3i128;
                r += small == 1024 && widened == 3072i128 && (i32)widened == 3072 && -a < 0i128 ? 100 : 0;
                i256 big = 1i256 << 200;
                i256 c = big * 3i256 - 1i256;
                r += c / big == 2i256 && c > big && (c & big) == 0i256 && (big | 1i256) - big == 1i256 ? 1000 : 0;
                u256 m = 0u256 - 1u256;
                r += (m >> 255) == 1u256 && m / (1u256 << 128) == (1u256 << 128) - 1u256 && Big8(a, 2i128) == b + 1i128 ? 10000 : 0;
                i128 neg = -7i128;
                r += neg / 2i128 == -3i128 && neg % 2i128 == -1i128 && (u128)(1i128 << 127) == 1u128 << 127 ? 100000 : 0;
                r += a.ToString() == "1267650600228229401496703205376" && (-5i128).ToString() == "-5" && Int128.MinValue.ToString() == "-170141183460469231731687303715884105728" ? 1000000 : 0;
                r += UInt256.MaxValue.ToString() == "115792089237316195423570985008687907853269984665640564039457584007913129639935" && (0u256).ToString() == "0" ? 10000000 : 0;
                r += Int128.Parse("-123456789012345678901234567890") == -123456789012345678901234567890i128 && UInt128.Parse("340282366920938463463374607431768211455") == UInt128.MaxValue ? 100000000 : 0;
                i256? none = Int256.TryParse("12x");
                i256? some = Int256.TryParse("-77");
                r += none == null && some != null && some.Value == -77i256 && Sum8(3i256, 4i256) == 7i256 && a.Hash() == (1u64 << 36) ? 1000000000 : 0;
                return r;
            }

            public WideFloats() : i32
            {
                i32 r = 0;
                f16 h = 1.5f16;
                r += h + h == 3f16 && Float16.Bits(0.1f16) == 0x2E66 && Float16.Bits(65504f16) == 0x7BFF && (f64)(1f16 / 3f16) == 0.333251953125 && (0.1f16).ToString() == "0.1" ? 1 : 0;
                r += (i32)(h * 2f16) == 3 && (f16)3.25 == 3.25f16 && (f32)h == 1.5f32 && h > 1f16 && h < 2f16 && Float16.Parse("65504") == Float16.MaxValue && (1f16 / 3f16).ToString() == "0.3333" ? 10 : 0;
                f128 q = 1.5f128;
                f128 qtenth = 0.1f128;
                r += Float128.Bits(q) == (u128)0x3FFF8 << 108 && Float128.Bits(qtenth) == 0x3FFB999999999999999999999999999Au128 && q + q == 3f128 ? 100 : 0;
                f128 third = 1f128 / 3f128;
                r += third * 3f128 == 1f128 && (f64)third == 0.3333333333333333 && third < 0.5f128 && third != 0.3333333333333333 && third.ToString() == "0.3333333333333333333333333333333333" ? 1000 : 0;
                i128 big = (i128)(1f128 * 1e30f128);
                f64 tenth = 0.1;
                r += big == 1000000000000000000000000000000i128 && (f128)1000000000000000000000000000000i128 == 1e30f128 && (i32)2.75f128 == 2 && (i32)(-2.75f128) == -2 && (f128)tenth != qtenth && (f64)qtenth == 0.1 ? 10000 : 0;
                r += Float128.MaxValue.ToString() == "1.189731495357231765085759326628007E+4932" && Float128.Epsilon.ToString() == "6E-4966" && (-123456.789f128).ToString() == "-123456.789" && third.ToString("F20") == "0.33333333333333333333" ? 100000 : 0;
                r += Float128.Parse("123456789012345678901234567890.5e-10") == 123456789012345678901234567890.5e-10f128 && Float128.Parse("1.0e-4950") == 1e-4950f128 && Float128.TryParse("x") == null && Float128.IsNaN(Float128.NaN) && Float128.NaN.Equals(Float128.NaN) ? 1000000 : 0;
                f256 o = 1.5f256;
                f256 otenth = 0.1f256;
                f256 oh = 1f256 / 3f256;
                r += o + o == 3f256 && Float256.Bits(o) == (u256)0x3FFFF8 << 232 && (f128)otenth == qtenth && (f256)qtenth != otenth && oh * 3f256 == 1f256 && (i64)(oh * 3e18f256) == 1000000000000000000 ? 10000000 : 0;
                r += otenth.ToString() == "0.1" && oh.ToString() == "0.333333333333333333333333333333333333333333333333333333333333333333333333" && Float256.Parse("0.1") == otenth && oh.ToString("E5") == "3.33333E-001" ? 100000000 : 0;
                f128 nan = 0f128 / 0f128;
                r += 7.5f128 % 2f128 == 1.5f128 && -q == -1.5f128 && !(nan == nan) && nan != nan && !(nan < q) && 1f128 / 0f128 > 1e4000f128 ? 1000000000 : 0;
                return r;
            }

            record class Point
            {
                public:
                    i32 X;
                    i32 Y;
                    str Name;

                    new(i32 x, i32 y, str name)
                    {
                        X = x;
                        Y = y;
                        Name = name;
                    }
            }

            record value Size
            {
                public:
                    i32 W;
                    i32 H;

                    new(i32 w, i32 h)
                    {
                        W = w;
                        H = h;
                    }
            }

            record class Box
            {
                public:
                    Point Corner;
                    Size Extent;

                    new(Point corner, Size extent)
                    {
                        Corner = corner;
                        Extent = extent;
                    }
            }

            class Pair
            {
                public:
                    i32 A;
                    i32 B;

                    new(i32 a, i32 b)
                    {
                        A = a;
                        B = b;
                    }

                    Deconstruct(&mut i32 a, &mut i32 b)
                    {
                        a = A;
                        b = B;
                    }
            }

            public Records() : i32
            {
                i32 r = 0;
                Point p = new Point(1, 2, "p");
                Point q = new Point(1, 2, "p");
                r += p == q && p != new Point(2, 2, "p") && p != new Point(1, 2, "q") ? 1 : 0;
                Point moved = p with { X = 5, Name = "m" };
                r += moved.X == 5 && moved.Y == 2 && moved.Name == "m" && p.X == 1 && p.Name == "p" ? 10 : 0;
                Size s = new Size(3, 4);
                Size t = s with { H = 9 };
                r += s == new Size(3, 4) && t.H == 9 && t.W == 3 && s != t ? 100 : 0;
                r += p is Point(1, var y, _) && y == 2 ? 1000 : 0;
                r += s is (3, 4) && !(s is (4, 3)) ? 10000 : 0;
                r += p is { X: 1, Y: > 1 } && !(p is { Y: 5 }) && p is { Name: var nm } && nm == "p" ? 100000 : 0;
                i32 k = p switch { Point(0, _, _) => 1, Point(var x, 2, _) => x + 10, _ => 0 };
                r += k == 11 ? 1000000 : 0;
                Box box = new Box(p, s);
                Box same = new Box(new Point(1, 2, "p"), new Size(3, 4));
                r += box == same && box != (box with { Extent = t }) && box is Box(Point(1, _, _), (3, var h)) && h == 4 ? 10000000 : 0;
                Pair pair = new Pair(7, 8);
                r += pair is (7, var b) && b == 8 && pair is Pair(_, 8) ? 100000000 : 0;
                any o = p;
                r += o is Point(1, 2, _) && !(o is Size(3, 4)) ? 1000000000 : 0;
                return r;
            }

            record class Named
            {
                public:
                    str Tag;
                    Size Extent;

                    new(str tag, Size extent)
                    {
                        Tag = tag;
                        Extent = extent;
                    }

                    override ToString() : str => "<" + Tag + ">";
            }

            public RecordMembers() : i32
            {
                i32 r = 0;
                Point p = new Point(1, 2, "p");
                Point q = new Point(1, 2, "p");
                Point moved = p with { X = 5 };
                r += p.ToString() == "Point { X = 1, Y = 2, Name = p }" && (new Size(3, 4)).ToString() == "Size { W = 3, H = 4 }" ? 1 : 0;
                r += p.Hash() == q.Hash() && p.Hash() != moved.Hash() && p.Equals(q) && !p.Equals(moved) ? 10 : 0;
                r += p is { Name: "p" } && !(p is { Name: "q" }) && p is Point(_, _, "p") ? 100 : 0;
                i32 k = p switch { Point(_, _, "q") => 1, Point(_, _, "p") => 2, _ => 0 };
                str? none = null;
                str some = "abc";
                r += k == 2 && !(none is "x") && some is "abc" && (some switch { "x" => 1, "abc" => 2, _ => 3 }) == 2 ? 1000 : 0;
                any o = p;
                Named n = new Named("t", new Size(1, 1));
                r += o is IToString && n.ToString() == "<t>" && n.Hash() == (new Named("t", new Size(1, 1))).Hash() && n == new Named("t", new Size(1, 1)) ? 10000 : 0;
                return r;
            }

            record class Person
            {
                public:
                    str Name;
                    str? Nick;
                    i32? Age;
                    i32[] Tags;

                    new(str name, str? nick, i32? age, i32[] tags)
                    {
                        Name = name;
                        Nick = nick;
                        Age = age;
                        Tags = tags;
                    }
            }

            record value GridCell
            {
                public:
                    i32 Row;
                    i32 Col;

                    new(i32 row, i32 col)
                    {
                        Row = row;
                        Col = col;
                    }
            }

            public RecordFields() : i32
            {
                i32 r = 0;
                Person a = new Person("ann", null, null, [1, 2]);
                Person b = new Person("ann", null, null, [1, 2]);
                Person c = new Person("ann", "nn", 3, [1, 2]);
                Person d = new Person("ann", "nn", 3, [1, 3]);
                r += a == b && a != c && c != d && c == (d with { Tags = [1, 2] }) ? 1 : 0;
                r += a.Hash() == b.Hash() && a.Hash() != c.Hash() && c.Hash() != d.Hash() ? 10 : 0;
                r += a.ToString() == "Person { Name = ann, Nick = null, Age = null, Tags = [1, 2] }" && c.ToString() == "Person { Name = ann, Nick = nn, Age = 3, Tags = [1, 2] }" ? 100 : 0;
                Dictionary<Point, i32> byKey = new Dictionary<Point, i32>();
                byKey[new Point(1, 2, "a")] = 10;
                byKey[new Point(2, 2, "b")] = 20;
                byKey[new Point(1, 2, "a")] = 11;
                r += byKey.Count == 2 && byKey[new Point(1, 2, "a")] == 11 && byKey.ContainsKey(new Point(2, 2, "b")) && !byKey.ContainsKey(new Point(2, 2, "c")) ? 1000 : 0;
                HashSet<GridCell> cells = new HashSet<GridCell>();
                cells.Add(new GridCell(1, 2));
                cells.Add(new GridCell(1, 2));
                cells.Add(new GridCell(2, 1));
                r += cells.Count == 2 && cells.Contains(new GridCell(2, 1)) && !cells.Contains(new GridCell(3, 3)) ? 10000 : 0;
                return r;
            }

            public value Vec3<T : snumber> : ISignedNumber<Vec3<T>>
            {
                public T X, Y, Z;

                public new(T x, T y, T z);
                public new(T x, T y) : (x, y, T.Zero);

                public T SquaredMagnitude { get => (X * X) + (Y * Y) + (Z * Z); }

                public static Vec3<T> One { get => new Vec3<T>(T.One, T.One, T.One); }
                public static Vec3<T> Zero { get => new Vec3<T>(T.Zero, T.Zero, T.Zero); }
                public static Vec3<T> NegativeOne { get => new Vec3<T>(T.NegativeOne, T.NegativeOne, T.NegativeOne); }
                public static FromF64(f64 value) : Vec3<T> => new Vec3<T>(T.FromF64(value), T.FromF64(value), T.FromF64(value));
                public static ToF64(Vec3<T> v) : f64 => Math.Sqrt(T.ToF64(v.SquaredMagnitude));
                public static Twice() : Vec3<T> => One + One;
                public static Count<U>(U[] items) : i32 => items.Length;

                public op +(Vec3<T> left, Vec3<T> right) : Vec3<T> => new Vec3<T>(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
                public op -(Vec3<T> left, Vec3<T> right) : Vec3<T> => new Vec3<T>(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
                public op *(Vec3<T> left, Vec3<T> right) : Vec3<T> => new Vec3<T>(left.X * right.X, left.Y * right.Y, left.Z * right.Z);
                public op /(Vec3<T> left, Vec3<T> right) : Vec3<T> => new Vec3<T>(left.X / right.X, left.Y / right.Y, left.Z / right.Z);
                public op %(Vec3<T> left, Vec3<T> right) : Vec3<T> => new Vec3<T>(left.X % right.X, left.Y % right.Y, left.Z % right.Z);
                public left op -(Vec3<T> operand) : Vec3<T> => new Vec3<T>(-operand.X, -operand.Y, -operand.Z);
                public op ==(Vec3<T> left, Vec3<T> right) : bool => left.X == right.X && left.Y == right.Y && left.Z == right.Z;
                public op <(Vec3<T> left, Vec3<T> right) : bool => left.SquaredMagnitude < right.SquaredMagnitude;
            }

            public class Tally<T : number>
            {
                public:
                    static T Sum;
                    static i32 Count;

                    static Record(T value)
                    {
                        Sum += value;
                        Count++;
                    }
            }

            SumOf<V : ISignedNumber<V>>(V a, V b) : V => a + b;

            public GenericStatics() : i32
            {
                i32 r = 0;
                Vec3<i32> a = Vec3<i32>.One;
                Vec3<i32> z = Vec3<i32>.Zero;
                r += a.X == 1 && a.Y == 1 && a.Z == 1 && z.X == 0 && a.X.ToString("X4") == "0001" ? 1 : 0;
                Vec3<i32> b = new Vec3<i32>(2, 3);
                Vec3<i32> c = a + b;
                r += c.X == 3 && c.Y == 4 && c.Z == 1 && (c - a) == b && (-a) == Vec3<i32>.NegativeOne && (a * b).Y == 3 && (b / a).X == 2 ? 10 : 0;
                r += a < b && !(b < a) && b.SquaredMagnitude == 13 && SumOf(a, b) == c && SumOf(Vec3<i32>.One, Vec3<i32>.One).Z == 2 && Vec3<i32>.Twice() == c - b + a ? 100 : 0;
                Vec3<f64> f = Vec3<f64>.One + new Vec3<f64>(0.5, 0.25);
                r += f.X == 1.5 && f.Y == 1.25 && f.Z == 1.0 && Vec3<f64>.Zero.X == 0.0 && Vec3<i32>.Count<str>(["a", "b"]) == 2 && Vec3<i32>.Count([1, 2, 3]) == 3 ? 1000 : 0;
                Tally<i32>.Record(4);
                Tally<i32>.Sum += 6;
                Tally<i64>.Record(100);
                Tally<i32>.Count++;
                Tally<i64>.Sum = Tally<i64>.Sum * 2;
                Tally<f64>.Count = Tally<i32>.Count + 1;
                r += Tally<i32>.Sum == 10 && Tally<i32>.Count == 2 && Tally<i64>.Sum == 200 && Tally<i64>.Count == 1 && Tally<f64>.Count == 3 && Tally<f64>.Sum == 0.0 ? 10000 : 0;
                return r;
            }

            Widen<T : number, U : number>(U value) : T => (T)value;
            MixIn<T : snumber, U : number>(T a, U b) : T => a + (T)b;
            HalfValue<T : [i32, i64, f64]>(T value) : f64 => (f64)value / 2.0;
            ScaleBy<T : number>(T value, i32 factor) : T => value * (T)factor;
            AsLong<T : number>(T value) : i64 => (i64)value;

            TwiceVia<T : number, U : number>(U value) : U
            {
                T t = (T)value;
                return (U)(t + t);
            }

            public GenericCasts() : i32
            {
                i32 r = 0;
                r += Widen<i64, i32>(7) == 7 && Widen<f64, i32>(3) == 3.0 && Widen<i32, f64>(2.75) == 2 && Widen<u8, i32>(300) == 44 ? 1 : 0;
                r += MixIn<i32, f64>(1, 2.5) == 3 && MixIn<f64, i32>(1.5, 2) == 3.5 ? 10 : 0;
                r += HalfValue<i32>(5) == 2.5 && HalfValue<f64>(3.0) == 1.5 ? 100 : 0;
                r += ScaleBy<f64>(1.5, 4) == 6.0 && ScaleBy<i64>(3, 5) == 15 && AsLong<f64>(9.9) == 9 && AsLong<u16>(65535) == 65535 ? 1000 : 0;
                r += TwiceVia<i64, i32>(21) == 42 && TwiceVia<f64, i32>(5) == 10 && TwiceVia<i32, f64>(1.5) == 2.0 ? 10000 : 0;
                return r;
            }

            class Vault2
            {
                friendly:
                    i32 secret;
                private:
                    i32 code;
                    class Hidden { public: static Four() : i32 => 4; }
                public:
                    new(i32 s) { secret = s; code = s + 1; Count = s + 2; }
                    i32 Count { get => code; private set { code = value; } }
                    static Inside() : i32 => Hidden.Four();
            }

            [FriendOf(Vault2)]
            class Reader2
            {
                public:
                    static Peek(Vault2 v) : i32 => v.secret;
            }

            base class Base2
            {
                inherited:
                    i32 n;
                public:
                    new(i32 n) { this.n = n; }
            }

            class Derived2 : Base2
            {
                public:
                    new() : base(3) { }
                    N() : i32 => n;
            }

            class Outer2
            {
                private:
                    static i32 hidden = 3;
                public:
                    class Inner { public: static Get() : i32 => Outer2.hidden; }
            }

            public Friends() : i32
            {
                i32 r = Reader2.Peek(new Vault2(7)) == 7 ? 1 : 0;
                r += new Derived2().N() == 3 && new Vault2(5).Count == 7 ? 10 : 0;
                r += Vault2.Inside() == 4 && Outer2.Inner.Get() == 3 ? 100 : 0;
                r += typeof(Reader2).IsFriendOf(typeof(Vault2)) && typeof(Vault2).IsFriendOf(typeof(Reader2)) && !typeof(Outer2).IsFriendOf(typeof(Vault2)) ? 1000 : 0;
                return r;
            }

            public value Pt1
            {
                public:
                    i32 X;
                    new(i32 x) { X = x; }
            }

            public value Plain8
            {
                public:
                    i32 A;
                    i32 B;
            }

            MakeIt<T : new(i32)>(i32 x) : T => new T(x);
            Fresh<T : new()>() : T => new T();
            Either<T : new(i32), new()>(bool which) : T => which ? new T(5) : new T();
            Width<T : fixed(8)>(T x) : i32 => (i32)sizeof(T);

            public NewAndFixed() : i32
            {
                i32 r = MakeIt<Bag>(3).V == 3 ? 1 : 0;
                r += Fresh<Bag>().V == 7 ? 10 : 0;
                r += Either<Bag>(true).V == 5 && Either<Bag>(false).V == 7 ? 100 : 0;
                r += MakeIt<Pt1>(4).X == 4 && Fresh<Plain8>().A == 0 ? 1000 : 0;
                r += Width<Plain8>(new Plain8()) == 8 && Width<u64>(1) == 8 ? 10000 : 0;
                return r;
            }

            public enum Levels { Pipe = 35 }

            public value Coins
            {
                public:
                    i64 Cents;
                    new(i64 cents) { Cents = cents; }
                    op +(Coins a, Coins b) : Coins => new Coins(a.Cents + b.Cents);
                    op ^^(Coins a, Coins b) : Coins precedence(PrecedenceLevel.Power) associativity(Associativity.Right) => new Coins(a.Cents * 10 + b.Cents);
                    op <=>(Coins a, Coins b) : i64 precedence(PrecedenceLevel.Relational) associativity(Associativity.None) => a.Cents - b.Cents;
                    op |>(Coins a, Coins b) : Coins precedence(Levels.Pipe) => new Coins(a.Cents * 100 + b.Cents);
                    right op !!(Coins a) : i64 => a.Cents * 2;
                    op ~~(Coins a) : Coins => new Coins(-a.Cents);
                    right op !(Coins a) : bool => a.Cents != 0;
                    op (i64)(Coins m) => m.Cents;
                    op (Coins)(i64 c) => new Coins(c);
            }

            TwiceOf<T : (i64)T>(T x) : i64 => (i64)x * 2;

            public CustomOperators() : i32
            {
                Coins a = new Coins(2), b = new Coins(3), c = new Coins(4);
                i32 r = (a ^^ b ^^ c).Cents == 54 ? 1 : 0;
                r += (a + b |> c).Cents == 504 ? 10 : 0;
                r += (a ^^ b + c).Cents == 27 ? 100 : 0;
                r += a <=> b < 0 && b <=> a == 1 ? 1000 : 0;
                r += a!! == 4 && (~~a).Cents == -2 && (a~~).Cents == -2 && a! ? 10000 : 0;
                r += (i64)a == 2 && ((Coins)7).Cents == 7 ? 100000 : 0;
                r += TwiceOf(a) == 4 && TwiceOf((i32)21) == 42 ? 1000000 : 0;
                return r;
            }

            public enum Small : u8 { A, B = 200, C }
            public bitflag enum Flags : u16 { First, Second, Third, AllDeclared = declbits, All = allbits }
            public enum Word : str { Hello = "hola", World }

            public EnumBases() : i32
            {
                i32 r = (i32)Small.C == 201 && sizeof(Small) == 1 ? 1 : 0;
                Small s = (Small)200;
                r += s == Small.B && (i64)s == 200 ? 10 : 0;
                Flags f = Flags.First | Flags.Third;
                r += (u16)f == 5 && (u16)Flags.AllDeclared == 7 && (u16)Flags.All == 65535 && sizeof(Flags) == 2 ? 100 : 0;
                r += (~Flags.First & Flags.AllDeclared) == (Flags.Second | Flags.Third) ? 1000 : 0;
                r += (str)Word.Hello == "hola" && (str)Word.World == "World" && Word.Hello.ToString() == "Hello" ? 10000 : 0;
                Word w = (Word)"World";
                r += w == Word.World && (Word)"nope" != Word.Hello && (i32)(Word)"nope" == -1 ? 100000 : 0;
                r += w is Word.World && w != Word.Hello ? 1000000 : 0;
                r += f.ToString() == "First | Third" && Flags.Second.ToString() == "Second" ? 10000000 : 0;
                return r;
            }

            public NumberTypes() : i32
            {
                i32[] ints = [1, 2, 3, 4];
                f64[] floats = [0.5, 0.25];
                Cash[] money = [new Cash(150), new Cash(250)];
                i32 r = NumTotal(ints);
                r += (i32)(NumTotal(floats) * 4.0);
                r += (i32)NumTotal(money).cents;
                r += NumMax(3, 7);
                r += NumTwice(21);
                r += NumNeg(-5);
                r += (i32)NumNeg(new Cash(7)).cents;
                if (NumIsZero(0) && !NumIsZero(1.0) && NumIsZero(new Cash(0)))
                    r += 1000;
                if (NumTwice(1.5) == 3.0 && NumMax(2.5, 1.5) == 2.5 && NumNeg(Cash.One).cents == -100)
                    r += 10000;
                if (NumTotal(ints) != 10)
                    r += 100000;
                return r;
            }

            class ReactCounter
            {
                public:
                    static i32 Seen;
                    static i32 Runs;
                    static i32 Logged;
                    static i32 Watched;

                    reactive i32 count = 0;
                    computed i32 Double => count * 2;

                    new()
                    {
                        effect { Seen = Double; Runs++; }
                    }

                    reacts Log() { Logged += count + 1; }

                    Bump() { count = count + 1; count = count + 1; }
            }

            class ReactModel
            {
                public:
                    static i32 Evals;
                    static i32 Seen;

                    reactive i32 count = 1;
                    computed i32 Double => ReactTwice(count);
                    computed i32 Quad => Double * 2;

                    static ReactTwice(i32 v) : i32 { Evals++; return v * 2; }
            }

            public Computed() : i32
            {
                i32 r = 0;
                ReactModel m = new ReactModel();
                r += m.Double == 2 && m.Double == 2 && ReactModel.Evals == 1 ? 1 : 0;
                m.count = 5;
                r += ReactModel.Evals == 1 ? 10 : 0;
                r += m.Double == 10 && ReactModel.Evals == 2 ? 100 : 0;
                effect { ReactModel.Seen = m.Quad + 1; }
                r += ReactModel.Seen == 21 && ReactModel.Evals == 2 ? 1000 : 0;
                m.count = 7;
                r += ReactModel.Seen == 29 && ReactModel.Evals == 3 ? 10000 : 0;
                System.Reactive.Reactive.Batch(() => { m.count = 1; m.count = 2; });
                r += ReactModel.Seen == 9 && ReactModel.Evals == 4 ? 100000 : 0;
                return r;
            }

            class ReactSwitch
            {
                public:
                    static i32 Runs;
                    static i32 Evals;
                    static i32 Shown;

                    reactive bool useA = true;
                    reactive i32 a = 1;
                    reactive i32 b = 10;
                    computed i32 Pick => ReactCount(useA ? a : b);

                    static ReactCount(i32 v) : i32 { Evals++; return v; }
            }

            public Dependencies() : i32
            {
                i32 r = 0;
                ReactSwitch s = new ReactSwitch();
                Effect e = effect { ReactSwitch.Runs++; ReactSwitch.Shown = s.useA ? s.a : s.b; };
                r += ReactSwitch.Runs == 1 && ReactSwitch.Shown == 1 && e.Sources == 2 ? 1 : 0;
                s.b = 20;
                r += ReactSwitch.Runs == 1 ? 10 : 0;
                s.useA = false;
                r += ReactSwitch.Runs == 2 && ReactSwitch.Shown == 20 && e.Sources == 2 ? 100 : 0;
                s.a = 5;
                r += ReactSwitch.Runs == 2 ? 1000 : 0;
                e.Dispose();
                s.b = 30;
                r += ReactSwitch.Runs == 2 && e.Sources == 0 ? 10000 : 0;
                r += s.Pick == 30 && ReactSwitch.Evals == 1 ? 100000 : 0;
                s.a = 7;
                r += s.Pick == 30 && ReactSwitch.Evals == 1 ? 1000000 : 0;
                s.useA = true;
                r += s.Pick == 7 && ReactSwitch.Evals == 2 ? 10000000 : 0;
                Computed<i32> c = new Computed<i32>(() => s.b * 2);
                r += c.Stale && c.Value == 60 && !c.Stale ? 100000000 : 0;
                c.Dispose();
                s.b = 50;
                r += !c.Stale && c.Value == 60 ? 1000000000 : 0;
                return r;
            }

            class ReactAuto
            {
                public:
                    static i32 Logged;
                    reactive i32 count = 3;
                    reacts Log() { Logged += count; }
            }

            public Reactive() : i32
            {
                i32 r = 0;
                ReactAuto auto = new ReactAuto();
                auto.count = 4;
                r += ReactAuto.Logged == 7 ? 100000 : 0;
                ReactCounter c = new ReactCounter();
                r += ReactCounter.Seen == 0 && ReactCounter.Runs == 1 && ReactCounter.Logged == 1 ? 1 : 0;
                c.count = 2;
                r += ReactCounter.Seen == 4 && ReactCounter.Runs == 2 && ReactCounter.Logged == 4 ? 10 : 0;
                Effect w = effect { ReactCounter.Watched = c.count + 100; };
                r += ReactCounter.Watched == 102 ? 100 : 0;
                System.Reactive.Reactive.Batch(c.Bump);
                r += c.count == 4 && ReactCounter.Runs == 3 && ReactCounter.Seen == 8 && ReactCounter.Watched == 104 && ReactCounter.Logged == 9 ? 1000 : 0;
                w.Dispose();
                c.count = 5;
                r += ReactCounter.Runs == 4 && ReactCounter.Seen == 10 && ReactCounter.Watched == 104 && ReactCounter.Logged == 15 ? 10000 : 0;
                return r;
            }

            public Resources() : i32
            {
                System.EmbeddedResources.Install(); // resources added at run time (the built-in ones come from the assembly: HiveTests)
                i32 r = 0;
                r += System.Resources.Has("greeting") && !System.Resources.Has("nope") && System.Resources.Names().Length == 2 ? 1 : 0;
                r += System.Resources.ReadText("greeting") == "hi" && System.Resources.Read("nope") == null && System.Resources.ReadText("nope") == null ? 10 : 0;
                if (System.Resources.Read("data/raw") is u8[] raw)
                    r += raw.Length == 21 && raw[20] == 255 && raw[7] == 7 ? 100 : 0;
                return r;
            }

            public Json() : i32
            {
                i32 r = 0;
                JsonValue v = JsonValue.Parse(" { \"name\": \"Ana\", \"age\": 31, \"tags\": [\"a\", \"b\"], \"ok\": true, \"none\": null, \"pi\": 3.5 } ");
                r += v.Kind == JsonKind.Object && v.Count == 6 && v.Get("name") is JsonValue n && n.AsString == "Ana" ? 1 : 0;
                r += v.Get("age") is JsonValue a && a.AsNumber == 31.0 && v.Get("tags") is JsonValue t && t.Count == 2 && t.At(1).AsString == "b" ? 10 : 0;
                r += v.Get("ok") is JsonValue o && o.AsBool && v.Get("none") is JsonValue z && z.IsNull && v.Get("missing") == null ? 100 : 0;
                r += v.ToString() == "{\"name\":\"Ana\",\"age\":31,\"tags\":[\"a\",\"b\"],\"ok\":true,\"none\":null,\"pi\":3.5}" ? 1000 : 0;
                JsonValue built = JsonValue.NewObject().Set("s", JsonValue.Of("q\"\n\\x")).Set("n", JsonValue.Of(-2)).Set("l", JsonValue.NewArray().Add(JsonValue.Of(1)).Add(JsonValue.Null()));
                str text = built.ToString();
                r += text == "{\"s\":\"q\\\"\\n\\\\x\",\"n\":-2,\"l\":[1,null]}" && JsonValue.Parse(text).ToString() == text ? 10000 : 0;
                JsonValue u = JsonValue.Parse("\"caf\\u00e9 \\ud83d\\ude00\"");
                r += u.AsString == "café 😀" ? 100000 : 0;
                bool failed = false;
                try
                {
                    JsonValue.Parse("[1, 2");
                }
                catch (FormatError e)
                {
                    failed = true;
                }
                r += failed ? 1000000 : 0;
                return r;
            }

            public Regexes() : i32
            {
                i32 r = 0;
                r += new Regex("a+b").IsMatch("xaab") && !new Regex("a+b").IsMatch("xb") ? 1 : 0;
                Regex phone = new Regex("^\\d{3}-\\d{4}$");
                r += phone.IsMatch("555-1234") && !phone.IsMatch("5551234") && !phone.IsMatch("x555-1234") ? 10 : 0;
                Match m = new Regex("(\\w+)@(\\w+)\\.com").Match("mail bob@site.com now");
                r += m.Success && m.Value == "bob@site.com" && m.Group(1) == "bob" && m.Group(2) == "site" && m.Index == 5 ? 100 : 0;
                r += new Regex("<.+?>").Match("<a><b>").Value == "<a>" && new Regex("cat|dog").Replace("cat and dog", "pet") == "pet and pet" ? 1000 : 0;
                r += new Regex("[^aeiou ]+").Match("hello world").Value == "h" && new Regex("[a-c]*").Match("abcd").Value == "abc"
                    && new Regex("(\\d+)-(\\d+)").Replace("1-2 and 30-40", "$2:$1") == "2:1 and 40:30" && new Regex("x?").Matches("ab").Count == 3 ? 10000 : 0;
                return r;
            }

            public Processes() : i32
            {
                i32 r = 0;
                bool windows = Environment.GetEnvironmentVariable("OS") == "Windows_NT";
                System.Diagnostics.ProcessResult result = windows
                    ? System.Diagnostics.Process.Run("cmd", "/c echo hi && exit 3")
                    : System.Diagnostics.Process.Run("/bin/sh", "-c \"echo hi; exit 3\"");
                r += result.Output.Trim() == "hi" ? 1 : 0;
                r += result.ExitCode == 3 ? 10 : 0;
                System.Diagnostics.Process two = windows
                    ? System.Diagnostics.Process.Start("cmd", "/c echo one && echo two")
                    : System.Diagnostics.Process.Start("/bin/sh", "-c \"echo one; echo two\"");
                str all = two.ReadAll();
                i32 code = two.WaitForExit();
                r += all.Contains("one") && all.Contains("two") && code == 0 ? 100 : 0;
                bool failed = false;
                try
                {
                    System.Diagnostics.Process.Start("no-such-program-xyz", "");
                }
                catch (IOError e)
                {
                    failed = true;
                }
                r += failed ? 1000 : 0;
                return r;
            }

            public Network() : i32
            {
                i32 r = 0;
                System.Net.TcpListener listener = new System.Net.TcpListener(0);
                i32 port = listener.Port;
                System.Threading.Thread echo = new System.Threading.Thread(() =>
                {
                    System.Net.TcpClient c = listener.Accept();
                    str got = c.Read(64);
                    c.Write("echo:" + got);
                    c.Close();
                });
                echo.Start();
                System.Net.TcpClient client = System.Net.TcpClient.Connect("127.0.0.1", port);
                client.Write("ping");
                str back = client.Read(64);
                client.Close();
                echo.Join();
                r += back == "echo:ping" ? 1 : 0;
                System.Threading.Thread server = new System.Threading.Thread(() =>
                {
                    System.Net.TcpClient c = listener.Accept();
                    str request = c.Read(1024);
                    c.Write(request.StartsWith("GET /x HTTP/1.0") ? "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello" : "HTTP/1.1 400 Bad\r\n\r\n");
                    c.Close();
                });
                server.Start();
                System.Net.HttpResponse response = System.Net.HttpClient.Get("http://127.0.0.1:" + port + "/x");
                server.Join();
                listener.Close();
                r += response.Status == 200 ? 10 : 0;
                r += response.Body == "hello" ? 100 : 0;
                return r;
            }

            public Exchange() : i32
            {
                i32 r = 0;
                u8* p = System.Exchange.Pack("héllo");
                r += System.Exchange.Length(p) == 6 ? 1 : 0;
                str back = System.Exchange.Text(p);
                r += back == "héllo" ? 10 : 0;
                u8* q = System.Exchange.PackBytes([1, 2, 3]);
                u8[] bytes = System.Exchange.Bytes(q);
                r += bytes.Length == 3 && bytes[2] == 3 ? 100 : 0;
                u8* e = System.Exchange.Pack("");
                r += System.Exchange.Length(e) == 0 && System.Exchange.Text(e) == "" ? 1000 : 0;
                return r;
            }

            [System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Explicit)]
            value LayNumber
            {
                public:
                    [System.Runtime.InteropServices.FieldOffset(0)] i64 Whole;
                    [System.Runtime.InteropServices.FieldOffset(0)] f64 Real;
                    [System.Runtime.InteropServices.FieldOffset(0)] u32 Low;
                    [System.Runtime.InteropServices.FieldOffset(4)] u32 High;
            }

            [System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Sequential, Pack = 1)]
            value LayPacked
            {
                public:
                    u8 Tag;
                    u32 Value;
                    u16 Short;
            }

            [System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Explicit, Size = 32)]
            value LayEvent
            {
                public:
                    [System.Runtime.InteropServices.FieldOffset(0)] u32 Type;
                    [System.Runtime.InteropServices.FieldOffset(8)] LayNumber Payload;
                    [System.Runtime.InteropServices.FieldOffset(16)] fixed u8[16] Name;
            }

            value LayHolder
            {
                public:
                    i32 Before;
                    LayEvent Inner;
                    i32 After;
            }

            public ValueLayouts() : i32
            {
                i32 r = 0;
                LayNumber n;
                n.Real = 2.5;
                r += n.Whole == 4612811918334230528 && n.High == 1074003968 && n.Low == 0 ? 1 : 0;
                n.Low = 7;
                n.High = 1;
                r += n.Whole == 4294967303 ? 10 : 0;
                r += sizeof(LayNumber) == 8 && sizeof(LayPacked) == 7 && sizeof(LayEvent) == 32 && sizeof(LayHolder) == 48 ? 100 : 0;
                LayPacked p;
                p.Tag = 9;
                p.Value = 0x01020304;
                p.Short = 0xBEEF;
                unsafe
                {
                    u8* raw = (u8*)&p;
                    r += raw[0] == 9 && raw[1] == 4 && raw[4] == 1 && raw[5] == 0xEF && raw[6] == 0xBE ? 1000 : 0;
                }
                LayHolder h;
                h.Before = -1;
                h.After = -2;
                h.Inner.Type = 0x8000;
                h.Inner.Payload.Real = 1.0;
                h.Inner.Name[0] = 65;
                h.Inner.Name[15] = 90;
                LayEvent copy = h.Inner;
                r += copy.Type == 0x8000 && copy.Payload.Real == 1.0 && copy.Name[0] == 65 && copy.Name[15] == 90 && h.Before == -1 && h.After == -2 ? 10000 : 0;
                unsafe
                {
                    u8* bytes = (u8*)&h.Inner;
                    r += bytes[16] == 65 && bytes[31] == 90 && *(u32*)bytes == 0x8000 ? 100000 : 0;
                }
                LayNumber m = n;
                m.High = 3;
                r += m.High == 3 && m.Low == 7 && n.High == 1 ? 1000000 : 0;
                return r;
            }

            static NumSpan<T : number>(T a, T b) : T => Math.Max(a, b) - Math.Min(a, b);
            static NumDist<T : snumber>(T a, T b) : T => Math.Abs(a - b);
            static NumClip<T : number>(T v, T lo, T hi) : T => Math.Clamp(v, lo, hi);
            static NumSign<T : snumber>(T v) : i32 => Math.Sign(v);
            static NumArraySum<T : number>(T[] items) : T => Array.Sum(items);
            static NumLow<T : number>(List<T> items) : T => Lists.Min(items);
            static NumLegacy<T : int>(T a, T b) : T => Math.Max(Math.Abs(a), b);
            static NumOnly<T : [i16, f32]>(T a) : T => Math.Abs(a);

            public NumberMath() : i32
            {
                i32 r = NumSpan(3, 10) == 7 ? 1 : 0;
                r += NumDist(2.5, 4.0) == 1.5 ? 10 : 0;
                r += NumClip(15u8, 1u8, 9u8) == 9 && NumClip(-2i64, 0i64, 4i64) == 0 ? 100 : 0;
                r += NumSign(-7) == -1 && NumSign(0.0) == 0 ? 1000 : 0;
                r += NumArraySum([1, 2, 3]) == 6 ? 10000 : 0;
                List<f64> l = new List<f64>();
                l.Add(2.5);
                l.Add(-1.0);
                r += NumLow(l) == -1.0 && Lists.Max(l) == 2.5 ? 100000 : 0;
                r += Math.Abs(-3) == 3 && Math.Max(2.0, 3.0) == 3.0 && Math.Max(7u32, 9u32) == 9 && Math.Gcd(12u64, 18u64) == 6 && Math.ISqrt(99i32) == 9 ? 1000000 : 0;
                r += NumLegacy(-9, 4) == 9 && NumOnly(-2.5f32) == 2.5f32 && NumOnly(-4i16) == 4 ? 10000000 : 0;
                // A program's own number: `>` from its `<`, `%` for Gcd, `-x` for Abs.
                r += Math.Max(new Cash(500), new Cash(700)).cents == 700 && Math.Gcd(new Cash(1200), new Cash(-1800)).cents == 600
                    && Math.Abs(new Cash(-300)).cents == 300 && Math.Sign(new Cash(-1)) == -1 && Math.Clamp(new Cash(900), new Cash(0), new Cash(500)).cents == 500 ? 100000000 : 0;
                return r;
            }

            public Bytes() : i32
            {
                i32 r = 0;
                u8[] a = [1, 2, 3, 4];
                u8[] b = [1, 2, 3, 4];
                u8[] c = [1, 2, 3, 5];
                u8[] none = [];
                u8[] none2 = [];
                r += System.Array.Equals(a, b) && !System.Array.Equals(a, c) && !System.Array.Equals(a, none) && System.Array.Equals(none, none2) && a != b ? 1 : 0;
                i32 more = 0, same = 0, less = 0;
                unsafe
                {
                    more = System.Runtime.InteropServices.Memory.Compare(&c[0], &a[0], 4);
                    same = System.Runtime.InteropServices.Memory.Compare(&a[0], &b[0], 4);
                    less = System.Runtime.InteropServices.Memory.Compare(&a[0], &c[0], 3);
                }
                r += more > 0 && same == 0 && less == 0 ? 10 : 0;
                str s = "h\u00e9llo w\u00f6rld";
                str t = "h\u00e9llo " + "w\u00f6rld";
                r += s == t && s.Length == t.Length && "" == "" && "abc" != "abd" && "abc".Length == 3 ? 100 : 0;
                System.Resources.Install("bytes", [9, 8, 7]);
                if (System.Resources.View("bytes") is System.ResourceData v)
                    r += v == [9, 8, 7] && v != [9, 8, 6] && v.Slice(1) == [8, 7] && v.Slice(3).Equals(none) && !(v.Slice(3) != none2) ? 1000 : 0;
                return r;
            }

            public Main() : i32 => Concat();
        }

        into namespace System
        {
            static class EmbeddedResources
            {
                public:
                    static Install()
                    {
                        Resources.Install("greeting", [104, 105]);
                        Resources.Install("data/raw", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 255]);
                    }
            }
        }
        """;

    /// <summary>The program's cases: each function, what it returns, what it tests.</summary>
    internal static IReadOnlyList<(string Function, int Expected, string What)> CaseList => Cases;

    private const string DestructorSource = """
        import System;

        into namespace Fin
        {
            class Res
            {
                public:
                    static i32 Closed;
                    static i32 Order;
                    str Name;

                    new(str name)
                    {
                        Name = name;
                    }

                    destroy()
                    {
                        Closed++;
                        Order = Order * 10 + 2;
                    }
            }

            class Sub : Res
            {
                public:
                    static i32 SubClosed;

                    new(str name) : base(name);

                    destroy()
                    {
                        SubClosed++;
                        Order = Order * 10 + 1;
                    }
            }

            class Plain
            {
                public:
                    i32 V;
                    new(i32 v) { V = v; }
            }

            Manual() : i32
            {
                Res a = new Res("a");
                Sub b = new Sub("b");
                Plain p = new Plain(1);
                Res c = b;
                delete a;
                delete c;
                delete p;
                return Res.Closed * 1000 + Sub.SubClosed * 100 + Res.Order;
            }

            Make(i32 n)
            {
                Res local = n == 0 ? new Res("x") : new Sub("y");
                Plain p = new Plain(n);
            }

            Counted() : i32
            {
                Make(0);
                i32 afterOne = Res.Closed;
                Make(1);
                return afterOne * 1000 + Res.Closed * 100 + Sub.SubClosed * 10 + (Res.Order == 212 ? 1 : 0);
            }

            Arena() : i32
            {
                region
                {
                    Res a = new Res("a");
                    Sub b = new Sub("b");
                    Plain p = new Plain(1);
                    if (p.V == 1 && Res.Closed != 0)
                        return -1;
                }
                i32 afterOne = Res.Closed;
                loop (i32 i = 0..3)
                {
                    region
                    {
                        Res c = new Res("c");
                        if (i == 1)
                            break;
                    }
                }
                return afterOne * 1000 + Res.Closed * 100 + Sub.SubClosed * 10 + (Res.Order == 21222 ? 1 : 0);
            }
        }
        """;

    /// <summary>`destroy()` under each GC mode: Manual on `delete`, RefCount when the last reference goes, Arena when the region closes.</summary>
    private void Destructors(List<SourceText> library)
    {
        var program = AntrilCompiler.Compile([.. library, new SourceText(DestructorSource, "fin.ant")], "Fin");
        Check("the destructor program compiles", program.Success, string.Join("; ", program.Diagnostics.Take(4)));
        if (!program.Success)
            return;
        object? Run(GcConfig gc, string function)
        {
            var lowered = HirLowerer.Lower(program.Assembly!, new LoweringOptions { Gc = gc });
            if (!lowered.Success)
            {
                Check($"lowers in {gc.Mode} mode", false, string.Join("; ", lowered.Errors.Take(3)));
                return null;
            }
            var compiled = Compiler.Compile(lowered.Module!, new CompilationOptions { Gc = gc });
            if (!compiled.Success)
            {
                Check($"compiles in {gc.Mode} mode", false, string.Join("; ", compiled.TypeErrors.Take(3).Select(e => e.ToString())));
                return null;
            }
            var vm = new Interpreter(compiled.Il!);
            return vm.Run(lowered.Module!.Functions.First(f => f.Name.EndsWith("." + function, StringComparison.Ordinal)).Name);
        }
        var arena = Run(GcConfig.Arena, "Arena");
        Check("Arena: a region's objects are destroyed when it closes (own destructor, then the base's; a `break` closes it too)", Equals(arena, 2411L) || Equals(arena, 2411), arena?.ToString());
        var escape = AntrilCompiler.Compile([.. library, new SourceText("import System;\ninto namespace Fin { class Box { public: i32 n; } Escape() : Box { region { Box b = new Box(); b.n = 1; return b; } } }\n", "escape.ant")], "Escape");
        Check("the escaping program compiles", escape.Success, string.Join("; ", escape.Diagnostics.Take(3)));
        if (escape.Success)
        {
            var escaped = HirLowerer.Lower(escape.Assembly!, new LoweringOptions { Gc = GcConfig.Arena });
            var checkedIr = escaped.Success ? Compiler.Compile(escaped.Module!, new CompilationOptions { Gc = GcConfig.Arena }) : null;
            Check("Arena: an object made in a region cannot be returned out of it (the IR region check)", checkedIr is { Success: false } && checkedIr.TypeErrors.Any(e => e.ToString().Contains("region")),
                checkedIr is null ? string.Join("; ", escaped.Errors.Take(2)) : string.Join("; ", checkedIr.TypeErrors.Take(2).Select(e => e.ToString())));
        }
        var manual = Run(GcConfig.Manual, "Manual");
        Check("Manual: `delete` runs the object's own destructor (a Sub through a Res reference too), then frees (2 closed, 1 Sub, order 2, 21, 212)", Equals(manual, 2312L) || Equals(manual, 2312), manual?.ToString());
        var counted = Run(GcConfig.RefCount, "Counted");
        Check("RefCount: a destructor runs when the last reference goes (1 after the first, 2 and 1 after the second, in order)", Equals(counted, 1211L) || Equals(counted, 1211), counted?.ToString());
    }

    private static readonly (string Function, int Expected, string What)[] Cases =
    [
        ("Concat", 11, "string literals and `+`"),
        ("Equality", 11, "`==` and `!=` compare the text"),
        ("Trimming", 116, "Trim, ToUpper, IndexOf(str)"),
        ("Substrings", 31111, "Substring, StartsWith, EndsWith, Contains, IndexOf(u16)"),
        ("Numbers", 17633, "integers to text and back (the smallest i64 too)"),
        ("Builder", 191, "StringBuilder"),
        ("Lists", 294, "List<i32>: Add, `loop (x in list)`, `list[i]`"),
        ("ListOps", 5244, "Insert, RemoveAt, Pop, `list[i] = v`"),
        ("StringList", 68, "List<str> and String.Join"),
        ("Maps", 23, "Dictionary<str, i32>: `map[k] = v`, `map[k]`, Set, Count"),
        ("IntMap", 500500, "Dictionary<i32, i32> with removals"),
        ("MapIterate", 63, "`loop (KeyValuePair<K, V> pair in map)`"),
        ("Sets", 132, "HashSet<str>"),
        ("Splitting", 114, "Split"),
        ("Errors", 1611, "IndexError, KeyNotFoundError, FormatError and its message"),
        ("Generic", 47, "a generic function constrained by IComparable<T> (on i32 and str)"),
        ("StringLoop", 3, "`loop (u16 c in text)`"),
        ("ToStrings", 52, "`+` calls ToString() (overridden, and a primitive's)"),
        ("GenericClass", 53, "a generic class over str and i32"),
        ("Inherit", 42, "a class deriving from a generic base, a virtual call through it"),
        ("Properties", 12168, "auto properties (instance and static), `+=` on a property, Int32.MaxValue"),
        ("Interfaces", 1025, "a program's interface as a constraint, met by a class and by a value type"),
        ("MathFns", 1417691, "Math: Sqrt, Round (to even), Floor, Pow, Sin, Abs, Max, Clamp, ISqrt, Gcd"),
        ("NameOfAndAny", 11, "nameof gives the last name written (a local, a member, a type, a namespace, a function); any is System.Any"),
        ("InPlaceArrays", 1111, "`fixed T[]` kept in the object (one allocation, `: fixed(n)` constructors), strings kept that way: long and short copies, Units as a copy, CopyTo, a part of an array, the builder, integers' digits"),
        ("FixedItables", 1111, "interface calls through the vtable's fixed entries: ten interfaces on eight entries, one redone in a derived class, IPut<i32> and IPut<str> on one class, `is`, a boxed value"),
        ("PrimitiveLimits", 111, "MinValue, MaxValue, Epsilon, NaN and the infinities are constants of every primitive (used in other constants), each one exact"),
        ("DeclaredKinds", 111, "`exception Missing` (MissingException, from System.Exception; a derived one), `attribute Note`, `T : exception` thrown, `T : attribute`, `into namespace` on a type's and a function's header"),
        ("BoxedValues", 11, "a function value and a pointer as an `any` (in a System.Boxed, shown by their type) and back"),
        ("Tuples", 11111, "tuples: `(i32, str)` and `(1, \"uno\")`, `==`, Dictionary keys, Tuple, positions, 17 items from the variadic fallback (carried by System.Core), `with`, null items, nested"),
        ("NameOfTypes", 11, "nameof of a type gives typeof(T).Name (Int32, List, String[]); of a type parameter, the type it is given"),
        ("MathWide", 1111, "f128/f256 Math in the type (Sqrt, Floor, Pow, Exp, Atan, Sin, Atan2 to every digit), u256 Sqrt by Newton far from a power of two"),
        ("PrimitiveText", 1, "TextWriter writes every primitive (u32, i8, u128, i256, f32 shortest, f16, f128); Console has an overload for each"),
        ("MathIntegers", 1111, "Math on integers is exact: Log/Log2/Log10 count divisions (1000 in base 10 is 3), Pow squares in the type (3^39 in i64), Sqrt near the type's largest value ends (no overflow); f64 unchanged"),
        ("MathGeneric", 11111, "every Math function on any INumber: integers (Sqrt is the integer root, Floor/Round give them back), f32, u128, a program's own number through FromF64/ToF64"),
        ("Doubles", 1111325, "Float64: Parse, NaN, ToString, infinity, Bits"),
        ("Chars", 81111, "Char: IsDigit, IsLetter, IsWhiteSpace, ToUpper, DigitValue"),
        ("Sorting", 1111, "Array.Sort (introsort), Lists.Sort of strings, BinarySearch"),
        ("RadixSorts", 111111, "Array.Sort of 1024+ integers by their bytes: i32 with negatives and i64 (as the comparison sort), u32 and u64 in unsigned order, a part of an array, equal and small values"),
        ("Collections2", 1011325, "Queue (a ring that grows) and Stack"),
        ("ListMore", 12542, "List: AddRange, Reverse, GetRange; Lists.IndexOf, Contains, Remove"),
        ("Utf8", 195141, "UTF-8: two-, three- and four-byte characters, back to the same text"),
        ("GenericText", 11111460, "String<T> over u8/u16/u32: str in UTF-8, conversions, code points, the same algorithms"),
        ("InterfaceValues", 111135819, "interface values: classes and boxed values in a List<IShape>, an extended interface, is/as, downcasts, a generic interface"),
        ("Boxing", 109413621, "values as `any`: boxed and unboxed, `is` patterns, ToString and Hash through the box, a value through its interface"),
        ("Strings2", 1131111, "PadLeft, Repeat, Split(str), Lines, LastIndexOf, Insert, Remove"),
        ("FileIO", 1313113, "File, Directory, Path, FileStream, StreamWriter, UTF-8 in files"),
        ("Streams", 111111111, "Stream: MemoryStream, BinaryWriter/Reader, StreamReader lines across buffers, BufferedStream, a program's own Stream, StringReader/Writer"),
        ("Paths", 111111, "Path: GetFileName, GetExtension, GetDirectoryName, ChangeExtension, Combine"),
        ("Environs", 1111, "Stopwatch, Thread.Sleep, Environment, GC"),
        ("Lambdas", 782112430, "lambdas: captured locals and `this`, a closure returned, method groups, nested lambdas"),
        ("Functional", 755531115, "List: Where, Select, Any, All, CountWhere, Aggregate, First, RemoveAll"),
        ("SortBy", 3251111, "List.Sort and Array.Sort with a comparison, Array.ForEach, FindIndex"),
        ("Nullables", 207511123, "nullable values: TryParse, `??`, `== null`, HasValue, Value, `is T v`"),
        ("Statics", 151711, "static constructors (in dependency order), nested types (of a generic type too), `++` on a static field"),
        ("Conditional", 1608534, "`?.` on fields, calls and strings, chained, with `??`"),
        ("Captures", 128912, "lambdas in loops: each pass's variables, a variable shared outside the loop"),
        ("Grids", 11123425, "N-dimensional arrays: new, literals, `[i, j]`, loops in row order, Length, Rank, GetLength"),
        ("Iterators", 11246, "`yield`: a range, a filter with `yield break`, a generic iterator, Next/Current by hand"),
        ("Linked", 1111, "LinkedList: add at both ends and next to a node, remove, Find; objects compared with ==, !="),
        ("SortedMaps", 11111, "SortedDictionary (AVL in arrays): order, Remove, MinKey, MaxKey, TryGet; SortedSet"),
        ("Priorities", 1111, "PriorityQueue (4-ary heap): Peek, Dequeue order, EnqueueDequeue, 1,000 random priorities"),
        ("Spans2", 1111, "Span: writes through to the array, Slice, Sort, Sum, IndexOf, index errors, a view of a List"),
        ("Threads", 11111, "threads: atomic counts, a Mutex, thread ids, Join with a timeout, compare-exchange"),
        ("Syncs", 111111, "SemaphoreSlim keeps two inside, CountdownEvent, ManualResetEvent and AutoResetEvent, BlockingCollection, Condition timeout"),
        ("Parallels", 11111, "Parallel.For (sums, Interlocked on array elements, an error thrown back), Parallel.Invoke, ConcurrentQueue"),
        ("TaskRuns", 11111, "Task.Run with and without a result, await of a pool task, WhenAll, a task waiting for another on the pool, a failed one"),
        ("Operators", 11111, "operators a value type declares (`op +`, unary `-` and `!`, `==`, `<`, `+=`; `!=` from `==`), and an enum's ToString"),
        ("Dates", 1111111, "DateTime: day of week, AddMonths/AddYears at month ends, differences, leap years, Unix time, parts, a date that does not exist"),
        ("TimeSpans", 1111111, "TimeSpan: From*, parts, + - unary - * /, ToString c/g/custom, Parse and TryParse"),
        ("DateTexts", 1111111, "DateTime as text (G, o, s, R, custom) and read back (Parse, TryParse with RoundtripKind, ParseExact)"),
        ("GenericMath", 1111111, "`T : numeric` / `float` / `integer`: Array and List Sum/Min/Max/Average, generic Math.Max/Abs/Clamp"),
        ("Checks", 1111111, "`checked`: +, * (64 bits), conversions, `++`, negation; `unchecked` wraps"),
        ("Randoms", 11111, "Random: the same numbers for a seed, ranges, Shuffle, NextBytes, an even spread"),
        ("Formats", 1111111, "number formats: X, N, F (ties to even), R, E, P, D, and interpolated strings with alignment"),
        ("Asyncs", 91742, "async/await: results, Task.Yield and Delay, errors through `await`, tasks taking turns, WhenAll, FromResult"),
        ("DefaultMethods", 11111, "interface default methods (instance, through the interface, a value's box and a constrained parameter; static) and `I<X>.Member` on a concrete interface"),
        ("NewAndFixed", 11111, "constraints `new(i32)` / `new()` (`new T(x)` on a class and a value), `fixed(8)` (a value and u64)"),
        ("CustomOperators", 1111111, "operators with new symbols (`^^`, `<=>`, `|>`) and declared precedence/associativity, `right op`, `op` on either side, conversions `op (i64)(Coins)` and the constraint `T : (i64)T`"),
        ("GenericOperators", 11111, "operators with their own type parameters (`op *<S : (T)S>`, inferred from the operands; a non-generic overload wins a tie), `op ++`/`op --` of a type (`c++` gives the old value, `++c` the new), conversions with type parameters (`op (Pair<U>)<U>(Box<U>)`, inferred from operand and target)"),
        ("FixedFields", 111, "`fixed i32[4]` and `fixed u8[sizeof(T)]` / `u8[2 * sizeof(T)]` fields (inline arrays: indexed, `.Length`, copied by value), and a generic function's `T` inferred from the expected result (`Bag b = MakeIt(3)`)"),
        ("Bitflags", 11111, "a bitflag enum's helpers: `HasFlag`, `HasAny`, `With`, `Without`, `Toggle`, and `ToString` naming the one-bit members it holds (`Read | Write`, the digits of unnamed bits, `0` for none)"),
        ("Literals", 111, "number literals with a type suffix (`200u8`, `1_000_i64`, `1.5f32`, `3f64`, trailing `_`), constructors without a body storing their parameters (also with `: base(id)`), `IToString` on a value and the renamed providers (Float64, UInt8)"),
        ("Extensions", 111111, "extension blocks: `ext(i32 n)`, `ext(Pt p)`, `static ext(i32)` (`i32.Answer()`), a generic `ext<T>(T[] items)` and `static ext<T : bitflag enum>(T)`; the core's `HasFlag`/`HasAny`/`With`/`Without`/`Toggle` as `ext<T : bitflag enum>`"),
        ("WideIntegers", 1111111111, "i128, u128, i256 and u256: literals, arithmetic, shifts, comparisons, division and remainder (signed), conversions to and from narrower types, and the providers Int128..UInt256 (ToString, Parse, TryParse, Hash, generic `snumber` use)"),
        ("WideFloats", 1111111111, "f16 (computed as f32 on x64), f128 and f256 (soft float): literals read exactly, arithmetic, comparisons (NaN unordered), remainder, conversions with integers and the other floats, and the providers Float16, Float128 and Float256 (Bits, constants, shortest and formatted ToString, Parse)"),
        ("Records", 1111111111, "`record class` and `record value`: `==` field by field (nested records, strings), `with` copies, positional patterns over the fields in order or a `Deconstruct(&mut …)`, property patterns, in `is` and `switch`, and through `any`"),
        ("RecordMembers", 11111, "records get `ToString()` (`Point { X = 1, … }`), `Hash()` and `Equals(T)` written for them (IToString, IHashable<T>), unless they declare their own; `str` constants in patterns (`is \"p\"`, `{ Name: \"p\" }`, switch arms), also on `str?`"),
        ("RecordFields", 11111, "records with nullable and array fields: `==`, `Hash()` and `ToString()` treat null as null, arrays element by element (System.Records); records as Dictionary keys and HashSet members"),
        ("GenericStatics", 11111, "static members of generic types named with their arguments: `Vec3<i32>.One` (a static property is its getter with the type's arguments), `Tally<i64>.Sum` (one global per instantiation, read, assigned, compound-assigned and stepped from outside), static methods with their own type parameters, operators of a generic value implementing ISignedNumber<Self>"),
        ("Reactive", 111111, "`reacts` in a class without a constructor of its own (the implicit one starts it); `reactive` fields (a Source tracked when read inside an effect, notifying when assigned), `computed` properties, `effect { }` run now and on each change (as a statement, or giving the Effect to Dispose), `reacts` methods run once the object is built, `Reactive.Batch` running each effect once at the end"),
        ("Computed", 111111, "`computed` with a cache (System.Reactive.Computed<T>): computed on the first read only, stale when something it read changes (not computed until read again), effects reading it rerun, a computed of a computed, one computation per Batch"),
        ("Dependencies", 1111111111, "an effect's dependencies are those of its last run only (a Source it stops reading no longer runs it; `Effect.Sources`), `Dispose` releases them; a computed depends on what its last computation read; `Computed<T>` used directly: `Stale`, `Value`, `Dispose` keeps the last value"),
        ("Resources", 111, "`System.Resources` with resources installed at run time (Install, Has, Names, Read as `u8[]?`, ReadText as `str?`); the built-in ones (the assembly's, through the `$resources.*` services) are tested by the Hive build"),
        ("Json", 1111111, "`System.Text.JsonValue`: Parse of objects, arrays, strings, numbers, bools and null (Kind, Count, Get/At, AsString/AsNumber/AsBool, IsNull), compact ToString, values built by hand (Set/Add) that round-trip, `\\u` escapes with a surrogate pair, FormatError on bad text"),
        ("Regexes", 11111, "`System.Text.Regex` (backtracking over UTF-8 units): literals, `.`, classes and ranges with negation, `\\d \\w \\s`, anchors, capturing and `(?:)` groups, alternatives, greedy and lazy quantifiers with bounds; IsMatch, Match (Index, Value, Group), Matches, Replace with `$n`"),
        ("Processes", 1111, "`System.Diagnostics.Process`: Run(command, arguments) with the output captured and the exit code, Start + ReadAll + WaitForExit, a program that cannot start throws IOError (cmd on Windows, /bin/sh elsewhere)"),
        ("Network", 111, "`System.Net`: a TcpListener on a port the system picks, a TcpClient that connects to it, an echo through NetworkStream from a thread, and HttpClient.Get against a one-request HTTP server on another thread (status 200, body)"),
        ("Exchange", 1111, "`System.Exchange`: packets (native memory, length + bytes) that carry strings and bytes between GC domains by copy: Pack(str)/PackBytes(u8[]), Length, Text and Bytes (which copy into this domain and free the packet), the empty packet"),
        ("ValueLayouts", 1111111, "`[ValueLayout]` / `[FieldOffset]`: an explicit union (i64, f64 and two u32 halves over the same bytes), `Pack = 1` (7 bytes, an unaligned u32), `Size = 32` with a nested laid-out value and an inline array at given offsets inside an ordinary value, copies, writes seen through pointers"),
        ("NumberMath", 111111111, "Math only over `T : number` / `T : snumber` (Abs on snumber; Min, Max, Clamp, Sign, Gcd, ISqrt on number: INumber's `<`, `%`, `-`, `T.Zero`, `>` made from `<`), Array and Lists (Sum, Min, Max), a program's own number (Cash), and still from `T : int` and `T : [i16, f32]`; Gcd and ISqrt on any number (a closed list meets an interface and its requirements when each of its types does)"),
        ("Bytes", 1111, "bytes compared in memory (IR `mem.cmp`): `Array.Equals(u8[], u8[])` (`==` stays identity), `Memory.Compare` with its sign, string literals through `$units` (one `mem.copy`), `ResourceData` `==`/`!=`/Equals against arrays"),
        ("GenericCasts", 11111, "casts between number type parameters and numbers: `(T)u` with `T, U : number`, `(T)n` from a number, `(i64)t` to one (each a conversion requirement the parameter promises, met per type argument by the numbers' own casts)"),
        ("Friends", 1111, "access: `friendly` with `[FriendOf]` (and Type.IsFriendOf), `inherited` from a derived class, a private setter and nested type inside their type, an outer's private from a nested type"),
        ("DefaultAccessors", 111111, "interface accessors with a body (defaults, also in a generic interface), `base get`/`base set` required of implementers, `base.M()` and `base.Prop` from the replacing member to the interface's default (and to a base class's virtual), an extending interface's default replacing the extended one's"),
        ("EnumBases", 11111111, "enums with a base (`: u8`, `: str`), bitflag enums with `~`, `declbits` and `allbits`, conversions through the base, texts of a string-based enum"),
        ("NumberTypes", 11460, "`T : number` / `snumber`: INumber<T> and ISignedNumber<T> on i32, f64 and a program's own value (operators, T.Zero, T.One, default(T))"),
    ];

    private void Programs(List<SourceText> library)
    {
        var compiled = CompiledProgram(library);
        Run(compiled.Assembly!, program.Value.Ms, library);
    }

    /// <summary>System.Core and the program, compiled once for the run (the test stops here when they do not compile).</summary>
    private AntrilResult CompiledProgram(List<SourceText> library)
    {
        var compiled = program.Value.Result;
        Check("System.Core and the program compile", compiled.Success, string.Join("\n    ", compiled.Diagnostics.Take(12)));
        Require();
        return compiled;
    }

    /// <summary>
    /// The same cases compiled with reference counting on the x64 and LLVM back ends: handles passed at +0, handles read
    /// from fields borrowed, objects released when their count reaches 0. Each gives what it gives with tracing.
    /// </summary>
    private void RunCounted(Larva.Hir.HirAssembly assembly)
    {
        var lowered = HirLowerer.Lower(assembly, new LoweringOptions { Gc = GcConfig.RefCount });
        if (!lowered.Success)
        {
            Check("RefCount: the program lowers", false, string.Join("; ", lowered.Errors.Take(4)));
            return;
        }
        var compilation = new CompilationOptions { Gc = GcConfig.RefCount };
        var x64 = X64Backend.Compile(lowered.Module!, new X64Options { Compilation = compilation });
        var llvm = LlvmBackend.Compile(lowered.Module!, new LlvmOptions { OptimizationLevel = 2, Compilation = compilation });
        Check("RefCount: x64 and LLVM compile it", x64.Success && llvm.Success, $"{x64.Error} {llvm}");
        if (!x64.Success || !llvm.Success)
            return;
        Larva.Native.NativeRuntime.Reset();
        using var image = x64.Program!.Load();
        using var program = llvm.Program!;
        using var jit = program.Jit();
        ((delegate* unmanaged<void>)image.Address(HirLowerer.StaticInitName))();
        ((delegate* unmanaged<void>)jit.Address(HirLowerer.StaticInitName))();
        var wrong = new List<string>();
        foreach (var (name, expected, _) in Cases)
        {
            if (CountedApart.Contains(name))
                continue;
            string function = $"CoreTest.$Module.{name}";
            long onX64 = ((delegate* unmanaged<int>)image.Address(function))();
            long onLlvm = ((delegate* unmanaged<int>)jit.Address(function))();
            if (onX64 != expected || onLlvm != expected)
                wrong.Add($"{name}: x64 {onX64}, LLVM {onLlvm} (not {expected})");
        }
        Check($"RefCount: the {Cases.Length - CountedApart.Count} cases give the same results on x64 and LLVM", wrong.Count == 0 && Larva.Native.NativeRuntime.Errors == 0,
            string.Join("; ", wrong.Take(6)) + $" errors {Larva.Native.NativeRuntime.Errors}");
    }

    /// <summary>Cases about the tracing collector itself (what reference counting does not have).</summary>
    private static readonly HashSet<string> CountedApart = [];

    /// <summary>
    /// The same program compiled against System.Core as a precompiled <c>.lva</c> and linked with it: the same results,
    /// and what each step costs next to compiling the library's source with the program.
    /// </summary>
    private void Linked(List<SourceText> library)
    {
        var watch = Stopwatch.StartNew();
        var core = AntrilCompiler.Compile(library, "System.Core");
        long coreFrontend = watch.ElapsedMilliseconds;
        Check("System.Core compiles on its own", core.Success, string.Join("; ", core.Diagnostics.Take(6)));
        if (!core.Success)
            return;
        watch.Restart();
        byte[] coreBytes = Larva.Hir.Encoding.LvaFormat.Write(core.Assembly!);
        long write = watch.ElapsedMilliseconds;
        // System.Core as it ships: a DLL carrying the assembly (larva lib -o System.Core.dll).
        byte[] dll = Larva.Hir.Encoding.LvaContainer.ToDll(coreBytes);
        string dllPath = Path.Combine(Path.GetTempPath(), $"larva-core-{Environment.ProcessId}.dll");
        File.WriteAllBytes(dllPath, dll);
        bool asLibrary = Larva.Hir.Encoding.LvaContainer.IsAssembly(dllPath) && Larva.Build.QueenProject.IsAssembly(dllPath);
        File.Delete(dllPath);
        string native = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        Check("System.Core.dll: a PE DLL whose .lva section reads back as the same assembly",
            dll[0] == 'M' && dll[1] == 'Z' && Larva.Hir.Encoding.LvaContainer.Unwrap(dll).AsSpan().SequenceEqual(coreBytes)
            && Larva.Hir.Encoding.LvaFormat.Read(dll).Types.Count == core.Assembly!.Types.Count && asLibrary
            && !Larva.Build.QueenProject.IsAssembly(native) && !Larva.Hir.Encoding.LvaContainer.IsAssembly(native));
        // The program's build: read the library's metadata, compile against it, link.
        long best = long.MaxValue, read = 0, frontend = 0, link = 0;
        Larva.Hir.Linking.HirLinkResult? linked = null;
        AntrilResult? program = null;
        for (int run = 0; run < 3; run++)
        {
            watch.Restart();
            var reference = Larva.Hir.Encoding.LvaFormat.Read(coreBytes);
            long r = watch.ElapsedMilliseconds;
            var p = AntrilCompiler.Compile([new SourceText(ProgramSource, "core.ant")], [reference], "CoreTest");
            long f = watch.ElapsedMilliseconds - r;
            if (!p.Success)
            {
                program = p;
                break;
            }
            byte[] programBytes = Larva.Hir.Encoding.LvaFormat.Write(p.Assembly!);
            var l = Larva.Hir.Linking.HirLinker.Link("CoreTest", [coreBytes, programBytes]);
            long total = watch.ElapsedMilliseconds;
            if (total < best)
            {
                best = total;
                read = r;
                frontend = f;
                link = total - r - f;
                linked = l;
                program = p;
            }
        }
        Check("the program compiles against System.Core.lva", program?.Success == true, string.Join("\n    ", program?.Diagnostics.Take(8) ?? []));
        if (program?.Success != true || linked is null)
            return;
        Check("and links with it", linked.Success, string.Join("; ", linked.Errors.Take(6)));
        if (!linked.Success)
            return;
        // Recompiling the library would cost the library's frontend on top of the program's own: a margin of 15 ms rides out
        // the noise of single runs (the program's frontend sits at 19-23 ms, the library's at 18-36 ms).
        Check("the program's frontend skips the library's source", frontend < coreFrontend + 15, $"{frontend} ms (the library's: {coreFrontend} ms)");
        Run(linked.Assembly!, best, library);
    }

    private void Run(Larva.Hir.HirAssembly assembly, long frontend, List<SourceText> library)
    {
        var watch = Stopwatch.StartNew();
        var lowered = HirLowerer.Lower(assembly);
        long lowering = watch.ElapsedMilliseconds;
        Check("their HIR lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(6)));
        if (!lowered.Success)
            return;
        watch.Restart();
        var vm = Larva.IR.Compiler.Compile(lowered.Module!);
        long checks = watch.ElapsedMilliseconds;
        Check("its IR passes the type and borrow checks", vm.Success, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        watch.Restart();
        var x64 = X64Backend.Compile(lowered.Module!);
        long x64Ms = watch.ElapsedMilliseconds;
        watch.Restart();
        var llvm = LlvmBackend.Compile(lowered.Module!, new LlvmOptions { OptimizationLevel = 2 });
        long llvmMs = watch.ElapsedMilliseconds;
        Check("x64 and LLVM compile it", x64.Success && llvm.Success, $"{x64.Error} {llvm}");
        int lines = library.Sum(f => f.Text.Count(c => c == '\n'));
        if (!vm.Success || !x64.Success || !llvm.Success)
            return;
        // One interpreter runs every case: its step budget is for all of them together.
        var interpreter = new Interpreter(vm.Il!, options: new InterpreterOptions { StepLimit = 200_000_000 });
        using var image = x64.Program!.Load();
        using var program = llvm.Program!;
        using var jit = program.Jit();
        // Static constructors first (the program's Main would run them).
        interpreter.Run(HirLowerer.StaticInitName);
        ((delegate* unmanaged<void>)image.Address(HirLowerer.StaticInitName))();
        ((delegate* unmanaged<void>)jit.Address(HirLowerer.StaticInitName))();
        foreach (var (name, expected, what) in Cases)
        {
            string function = $"CoreTest.$Module.{name}";
            object? onVm;
            try
            {
                onVm = interpreter.Run(function);
            }
            catch (Exception e)
            {
                onVm = e.GetType().Name + ": " + e.Message;
            }
            long onX64, onLlvm;
            try
            {
                onX64 = ((delegate* unmanaged<int>)image.Address(function))();
                onLlvm = ((delegate* unmanaged<int>)jit.Address(function))();
            }
            catch (Exception e)
            {
                Check($"{name}() = {expected}: {what}", false, e.Message + (x64.Skipped.TryGetValue(function, out var why) ? $" (x64 skipped it: {why})" : ""));
                continue;
            }
            bool same = onVm is int or long && Convert.ToInt64(onVm) == expected && onX64 == expected && onLlvm == expected;
            Check($"{name}() = {expected}: {what}", same, $"LVM {onVm}, x64 {onX64}, LLVM {onLlvm}");
        }
    }

    /// <summary>Access across assemblies: a program's `[FriendOf(T)]` on a library type, a library's `friendly` member from a stranger, an internal library type.</summary>
    private void Friends(List<SourceText> library)
    {
        const string libSource = "import System;\ninto namespace Lib\n{\n\tpublic class Vault { friendly: i32 secret; public: new(i32 s) { secret = s; } }\n\t[FriendOf(Vault)] public class Keeper { public: static Peek(Vault v) : i32 => v.secret; }\n}\n";
        var lib = AntrilCompiler.Compile([.. library, new SourceText(libSource, "lib.ant")], "FriendLib");
        Check("a library with friends compiles", lib.Success, string.Join("; ", lib.Diagnostics.Take(4)));
        if (!lib.Success)
            return;
        var reference = Larva.Hir.Encoding.LvaFormat.Read(Larva.Hir.Encoding.LvaFormat.Write(lib.Assembly!));
        AntrilResult Against(string text, string name) => AntrilCompiler.Compile([new SourceText("import System;\nimport Lib;\n" + text, name + ".ant")], [reference], name);
        var friend = Against("[FriendOf(Vault)] class Reader { public: static Peek(Vault v) : i32 => v.secret; }\nMain() : i32 => Reader.Peek(new Vault(1));\n", "Friend");
        Check("a program's `[FriendOf]` on a library type sees its `friendly` members", friend.Success, string.Join("; ", friend.Diagnostics.Take(4)));
        var stranger = Against("class Reader { public: static Peek(Vault v) : i32 => v.secret; }\nMain() : i32 => Reader.Peek(new Vault(1));\n", "Stranger");
        Check("AN1401: a library's `friendly` member from a type that is not a friend", !stranger.Success && stranger.Diagnostics.Any(d => d.Code == "AN1401"), string.Join("; ", stranger.Diagnostics.Take(4)));
        var internalUse = Against("Main() : i32 { System.Runtime.InteropServices.CStrings.Free(null); return 0; }\n", "Internal");
        Check("AN1400: an internal type of another assembly", !internalUse.Success && internalUse.Diagnostics.Any(d => d.Code == "AN1400"), string.Join("; ", internalUse.Diagnostics.Take(4)));
    }

    /// <summary>Wrong uses of generics, interfaces and properties: each gets its diagnostic (by code).</summary>
    private void Diagnostics(List<SourceText> library)
    {
        (string Source, string Code, string What)[] wrong =
        [
            ("class P { } F() : i32 { Dictionary<P, i32> m = new Dictionary<P, i32>(); return m.Count; }", "AN2094", "a type argument that does not implement the constraint"),
            ("F<T : snumber>(T x) : T => -x; G() : u8 => F((u8)1);", "AN2094", "an unsigned number is not an `snumber`"),
            ("value Bad : INumber<Bad> { public: i32 v; }", "AN1045", "a value that declares INumber without its operators and constants"),
            ("F<T : number>(T a, T b) : T => a & b;", "AN2078", "an operator the constraint does not give"),
            ("F<T : number>(T a) : T => -a;", "AN2063", "`-` needs `snumber`"),
            ("value Box<T : unmanaged> { public: T v; } F() : i32 { Box<str> b = new Box<str>(); return 0; }", "AN2102", "a type argument that is not of the constraint's kind"),
            ("enum E : u8 { A = 300 }", "AN1019", "an enum member that does not fit in the base"),
            ("class V { private: i32 c; } F(V v) : i32 => v.c;", "AN1401", "a private field from outside its type"),
            ("class V { friendly: i32 c; } F(V v) : i32 => v.c;", "AN1401", "a friendly field from a type that is not a friend"),
            ("base class V { inherited: i32 c; } F(V v) : i32 => v.c;", "AN1401", "an inherited field from outside the type and what derives from it"),
            ("class V { public: i32 C { get => 1; private set { } } } F(V v) { v.C = 2; }", "AN1401", "a private setter from outside its type"),
            ("class V { private: class H { } } F() : V.H? => null;", "AN1400", "a private nested type from outside its type"),
            ("[FriendOf(1)] class A { }", "AN1070", "`[FriendOf]` with something that is not a type"),
            ("[FriendOf(A)] class A { }", "AN1071", "a type that is its own friend"),
            ("enum E { A, B } F() : bool => E.A.HasFlag(E.B);", "AN2102", "`HasFlag` on an enum that is not a bitflag enum (the extension's constraint)"),
            ("bitflag enum E { A, B } F() : bool => E.A.HasFlag();", "AN2056", "`HasFlag` without the flags to test"),
            ("ext(i32 n) { public: i32 field; }", "AN1075", "a field in an extension block"),
            ("ext(i32 n) { public: static S() : i32 => 1; }", "AN1075", "a static function in an instance extension block"),
            ("ext(i32 n) { public: Clash(i32 n) : i32 => n; }", "AN1075", "a parameter named like the receiver"),
            ("static ext(i32 k) { }", "AN0029", "a static extension block with a receiver"),
            ("F() : u8[] => $resource(\"nope\");", "AN2139", "`$resource` naming a resource the program does not have"),
            ("reactive i32 n;", "AN0030", "`reactive` at namespace level"),
            ("value V { public: reactive i32 n; }", "AN0030", "`reactive` in a value"),
            ("interface I { computed i32 N => 1; }", "AN0030", "`computed` in an interface"),
            ("class C { public: static reactive i32 n; }", "AN0031", "a static `reactive` field"),
            ("class C { public: static reacts Log() { } }", "AN0031", "a static `reacts` method"),
            ("class C { public: computed i32 N { get => 1; } }", "AN0032", "`computed` with accessors"),
            ("class C { public: reactive i32 N { get => 1; set { } } }", "AN0032", "`reactive` with accessors"),
            ("class C { public: reacts Log(i32 x) { } }", "AN0033", "`reacts` with a parameter"),
            ("class C { public: reacts Log() : i32 => 1; }", "AN0033", "`reacts` with a result"),
            ("class C { public: reacts Log(); }", "AN0033", "`reacts` without a body"),
            ("value A { public: i32 n; } F(A a) : i32 => a.Missing();", "AN2053", "a method no type or extension has"),
            ("F() : u8 => 300u8;", "AN2028", "a suffixed literal that does not fit"),
            ("class A { public: i32 x; new(i32 x, i32 z); }", "AN2131", "a bodiless constructor with a parameter that names no field"),
            ("record class R { public: i32 x; } class D : R { }", "AN1076", "a class deriving from a record class"),
            ("class B { } record class R : B { public: i32 x; }", "AN1076", "a record class extending a class"),
            ("record class R { public: i32 x; new(i32 v) { x = v; } } F() { R r = new R(1); r.x = 2; }", "AN2070", "assigning a record's field (all are readonly)"),
            ("class C { public: i32 x; } F() : C => new C() with { x = 1 };", "AN2132", "`with` on a plain class"),
            ("record class R { public: i32 x; } F(R r) : R => r with { y = 1 };", "AN2133", "`with` naming a field the record lacks"),
            ("record class R { public: i32 x; } F(R? a, R? b) : bool => a == b;", "AN2134", "`==` on nullable records"),
            ("record class R { public: i32 x; } F(R r) : bool => r is (1, 2);", "AN2135", "a positional pattern with the wrong count"),
            ("class C { public: i32 x; } F(C c) : bool => c is (1, 2);", "AN2136", "a positional pattern on a type without positions"),
            ("record class R { public: i32 x; } F(R r) : bool => r is { y: 1 };", "AN2137", "a property pattern naming a member the type lacks"),
            ("value V { public: i32 x; } record class R { public: V v; }", "AN2138", "a record field without `Hash()` (no Hash can be written for the record)"),
            ("class Box<T> { public: static i32 count; } F() : i32 => Box.count;", "AN2091", "a generic type's static member without the type arguments"),
            ("value V { } F<T : number>(T x) : V => (V)x;", "AN2084", "a cast from a number parameter to a type that is no number"),
            ("value A { public: i32[4] cells; }", "AN1074", "an inline array field without `fixed`"),
            ("[System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Sequential)] class C { }", "AN1080", "`[ValueLayout]` on a class"),
            ("[System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Sequential)] value V<T> { public: T x; }", "AN1080", "`[ValueLayout]` on a generic value"),
            ("[System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Sequential)] value V { public: str s; }", "AN1081", "a field holding an object in a laid-out value"),
            ("[System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Explicit)] value V { public: i32 x; }", "AN1082", "an explicit value's field without `[FieldOffset]`"),
            ("[System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Sequential)] value V { public: [System.Runtime.InteropServices.FieldOffset(4)] i32 x; }", "AN1082", "`[FieldOffset]` in a sequential value"),
            ("value V { public: [System.Runtime.InteropServices.FieldOffset(0)] i32 x; }", "AN1082", "`[FieldOffset]` without `[ValueLayout]`"),
            ("[System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Sequential, Pack = 3)] value V { public: i32 x; }", "AN1083", "a Pack that is no power of two up to 16"),
            ("[System.Runtime.InteropServices.ValueLayout(System.Runtime.InteropServices.Layout.Explicit, Size = 16)] value V { public: [System.Runtime.InteropServices.FieldOffset(0)] i64 a; [System.Runtime.InteropServices.FieldOffset(0)] f64 b; [System.Runtime.InteropServices.FieldOffset(8)] fixed u8[8] c; func*<i32> : i32 f; }", "AN1082", "a func* field still needs its offset (the rest is accepted)"),
            ("value A { public: fixed i32 n; }", "AN1074", "`fixed` on a field that is not an inline array"),
            ("value A { public: fixed u8[0] z; }", "AN1073", "a fixed array of length 0"),
            ("value A { public: fixed u8[4] b; F() : i32 => b[1, 2]; }", "AN2043", "two indices on an inline array"),
            ("value A { public: op +<U>(A a, A b) : A => a; }", "AN1072", "an operator's type parameter that appears in no operand"),
            ("value A { public: i32 n; op ++(A a) : i32 => a.n; } F() { A a = new A(); a++; }", "AN2065", "an `op ++` that does not give back its operand's type"),
            ("value A { public: i32 n; } F() { A a = new A(); a++; }", "AN2065", "`++` on a type without `op ++`"),
            ("interface I { i32 X { base get; } } class A : I { }", "AN1045", "an interface property with a required getter left out"),
            ("interface I { i32 X { get => 1; base set; } } class A : I { public: i32 X { get => 2; } }", "AN1045", "an interface property's required setter left out"),
            ("interface I { M() : i32; } class A : I { public: M() : i32 => base.M(); }", "AN2049", "`base.M()` to an interface method without a body"),
            ("interface I { i32 X { base get; } } class A : I { public: i32 X { get => base.X; } }", "AN2049", "`base.X` to an interface getter without a body"),
            ("interface I { M() : i32 => 1; } class A : I { public: M() : i32 => base.N(); }", "AN2048", "`base.N()` that no base class or interface has"),
            ("interface I { M() : i32 => 1; } class A : I { public: static F() : i32 => base.M(); }", "AN2047", "`base` in a static function"),
            ("interface I { i32 X { base get => 1; } }", "AN0024", "a `base get` with a body"),
            ("interface IMake<T> { static Make() : T; } class A { } F() : A => IMake<A>.Make();", "AN2128", "`I<X>.Member` where no type argument implements the interface"),
            ("class A { } F<T : new(i32)>(i32 x) : T => new T(x); G() : A => F<A>(1);", "AN2127", "a type argument without the constructor the constraint asks"),
            ("class A { } F<T : new()>() : T => new T(1); G() : A => F<A>();", "AN2125", "`new T(…)` with arguments no constraint promises"),
            ("value P { public: i32 a; } F<T : fixed(8)>(T x) : T => x; G() : P => F(new P());", "AN2126", "a type argument of the wrong size"),
            ("value Box<T : unmanaged> { public: T v; } class C { public: Box<str> f; }", "AN2102", "a field of a generic type whose argument breaks the word constraint"),
            ("value Box<T : unmanaged> { public: T v; } F(Box<str> b) : i32 => 0;", "AN2102", "a parameter of a generic type whose argument breaks the word constraint"),
            ("value Box<T : unmanaged> { public: T v; } F() : i32 { Box<str> b = new Box<str>(); return 0; }", "AN2102", "a local of a generic type whose argument breaks the word constraint"),
            ("value A { public: op <>(A a, A b) : A => a; }", "AN1067", "a new symbol without its precedence"),
            ("value A { public: op +(A a, A b) : A precedence(10) => a; }", "AN1066", "a built-in symbol given a precedence"),
            ("value A { public: op ^^(A a, A b) : A precedence(10) => a; } value B { public: op ^^(B a, B b) : B precedence(20) => a; }", "AN1065", "a symbol declared with two precedences"),
            ("value A { public: right op %%(A a) : A precedence(10) => a; }", "AN1068", "a precedence on an operator with one operand"),
            ("value A { public: op <=>(A a, A b) : i64 precedence(100) associativity(Associativity.None) => 0; } F() : i64 { A a = new A(); return a <=> a <=> a; }", "AN0027", "chaining a non-associative operator"),
            ("value A { public: op ^^(A a, A b) : A precedence(10) => a; } value B { } F() : A { A a = new A(); B b = new B(); return b ^^ a; }", "AN2123", "an operator no operand type declares"),
            ("value A { } F<T : (str)T>(T x) : str => (str)x; G() : str => F(new A());", "AN2124", "a type argument without the conversion the constraint asks"),
            ("enum E : u8 { A = 255, B }", "AN1019", "an enum member past the base's last value"),
            ("enum E : str { A = 1 }", "AN1019", "a number in a string-based enum"),
            ("enum E : f64 { A }", "AN1006", "an enum based on something that is not an integer type or `str`"),
            ("bitflag enum E : str { A }", "AN1064", "a bitflag enum based on `str`"),
            ("enum E { A } F() : E => ~E.A;", "AN2064", "`~` on an enum that is not a bitflag enum"),
            ("class P { } Big<T : IComparable<T>>(T a) : T => a; F() : i32 { Big(new P()); return 0; }", "AN2094", "an inferred type argument that does not implement the constraint"),
            ("F() : i32 { List<i32, i32> l = new List<i32, i32>(); return 0; }", "AN1012", "the wrong number of type arguments"),
            ("F<T>(T a) : u64 => a.Hash();", "AN2092", "a method of an unconstrained type parameter"),
            ("Make<T>() : i32 => 0; F() : i32 => Make();", "AN2093", "type arguments that cannot be inferred"),
            ("class C { public: i32 X { get => 1; } } F() : i32 { C c = new C(); c.X = 2; return 0; }", "AN2095", "assigning a property without set"),
            ("class P : IHashable<P> { public: Hash() : u64 => 1; }", "AN1045", "an interface method left out"),
            ("F() : i32 { str s = \"a\"; return s * 2; }", "AN2080", "`*` on a string"),
            ("F() : i32 => Make<i32>(); Make<T>() : i32 => 1;", "", "explicit type arguments"),
            ("class C { public: i32 X { get; set; } } F() : i32 { C c = new C(); c.X = 5; c.X += 2; return c.X; }", "", "an auto property, assigned and compounded"),
        ];
        foreach (var (text, code, what) in wrong)
        {
            var result = AntrilCompiler.Compile([.. library, new SourceText("import System;\n" + text, "wrong.ant")], "Wrong");
            if (code.Length == 0)
            {
                Check($"accepted: {what}", result.Success, string.Join("; ", result.Diagnostics.Take(4)));
                continue;
            }
            Check($"{code}: {what}", !result.Success && result.Diagnostics.Any(d => d.Code == code),
                string.Join("; ", result.Diagnostics.Take(4).Select(d => d.ToString())));
        }
    }

    internal const string BenchSource = """
        import System;
        import System.IO;
        import System.Text;

        into namespace CoreBench
        {
            public Builder(i32 n) : i32
            {
                StringBuilder sb = new StringBuilder();
                loop (i32 i = 0..n)
                    sb.Append(i);
                return sb.Length;
            }

            public ListSum(i32 n) : i32
            {
                List<i32> list = new List<i32>();
                loop (i32 i = 0..n)
                    list.Add(i);
                i32 total = 0;
                loop (i32 x in list)
                    total ^= x;
                return total + list.Count;
            }

            public IntMap(i32 n) : i32
            {
                Dictionary<i32, i32> map = new Dictionary<i32, i32>();
                loop (i32 i = 0..n)
                    map[i * 7] = i;
                i32 found = 0;
                loop (i32 i = 0..n * 2)
                {
                    if (map.ContainsKey(i))
                        found++;
                }
                return found + map[7];
            }

            public StrMap(i32 n) : i32
            {
                Dictionary<str, i32> map = new Dictionary<str, i32>();
                loop (i32 i = 0..n)
                    map[i.ToString()] = i;
                i32 total = 0;
                loop (i32 i = 0..n)
                    total += map[i.ToString()] & 1;
                return total + map.Count;
            }

            public Words(i32 n) : i32
            {
                str text = "the quick brown fox jumps over the lazy dog";
                Dictionary<str, i32> counts = new Dictionary<str, i32>();
                loop (i32 round = 0..n)
                {
                    loop (str word in text.Split(' '))
                        counts[word] = counts.TryGet(word, 0) + 1;
                }
                return counts["the"] + counts.Count;
            }

            /// A large dictionary that stays, looked up with keys made for each lookup (garbage next to a big old heap).
            public Cache(i32 n) : i32
            {
                Dictionary<str, i32> map = new Dictionary<str, i32>();
                loop (i32 i = 0..500000)
                    map[i.ToString()] = i;
                i32 total = 0;
                loop (i32 i = 0..n)
                    total += map.TryGet((i % 600000).ToString(), 0) & 1;
                return total;
            }

            public SortInts(i32 n) : i32
            {
                i32[] numbers = new i32[n];
                i32 x = 12345;
                loop (i32 i = 0..n)
                {
                    x = x * 1103515245 + 12345;
                    numbers[i] = x >> 8 & 0xFFFFFF;
                }
                Array.Sort(numbers);
                return numbers[n / 2] + numbers[n - 1] % 1000;
            }

            public SortStrings(i32 n) : i32
            {
                List<str> words = new List<str>(n);
                loop (i32 i = 0..n)
                    words.Add((i * 7919 % n).ToString());
                Lists.Sort(words);
                return words[0].Length + words[n - 1].Length * 100 + words[n / 2].Length * 10000;
            }

            public Utf8Round(i32 n) : i32
            {
                StringBuilder sb = new StringBuilder();
                loop (i32 i = 0..n)
                    sb.Append(i % 7 == 0 ? (u16)233 : (u16)(97 + i % 26));
                str text = sb.ToString();
                i32 total = 0;
                loop (i32 round = 0..10)
                {
                    u8[] bytes = Encoding.UTF8.GetBytes(text);
                    total += bytes.Length + Encoding.UTF8.GetString(bytes).Length;
                }
                return total;
            }

            public FileLines(i32 n) : i32
            {
                str? temp = Environment.GetEnvironmentVariable("TEMP");
                str file = Path.Combine(temp is str found ? found : "/tmp", "larva-core-bench.txt");
                StreamWriter writer = new StreamWriter(file);
                loop (i32 i = 0..n)
                    writer.WriteLine("line " + i);
                writer.Close();
                i32 total = 0;
                loop (str line in File.ReadAllLines(file))
                    total += line.Length;
                File.Delete(file);
                return total;
            }

            public BinaryRound(i32 n) : i32
            {
                MemoryStream memory = new MemoryStream();
                BinaryWriter writer = new BinaryWriter(memory);
                loop (i32 i = 0..n)
                {
                    writer.Write(i);
                    writer.Write(i * 0.5);
                }
                memory.Position = 0;
                BinaryReader reader = new BinaryReader(memory);
                i64 total = 0;
                loop (i32 i = 0..n)
                    total += reader.ReadInt32() + (i64)reader.ReadFloat64();
                return (i32)(total % 1000000007);
            }

            public StreamLines(i32 n) : i32
            {
                str? temp = Environment.GetEnvironmentVariable("TEMP");
                str file = Path.Combine(temp is str found ? found : "/tmp", "larva-core-stream.txt");
                StreamWriter writer = new StreamWriter(file);
                loop (i32 i = 0..n)
                {
                    writer.Write("line ");
                    writer.Write(i);
                    writer.Write('\n');
                }
                writer.Close();
                i32 total = 0;
                StreamReader reader = new StreamReader(file);
                str? line = reader.ReadLine();
                loop (line is str text)
                {
                    total += text.Length;
                    line = reader.ReadLine();
                }
                reader.Close();
                File.Delete(file);
                return total;
            }

            public MathLoop(i32 n) : i32
            {
                f64 sum = 0;
                loop (i32 i = 0..n)
                    sum += Math.Sqrt((f64)i) + Math.Floor(i * 0.5);
                return (i32)((i64)sum % 1000000);
            }

            public SortBy(i32 n) : i32
            {
                i32[] numbers = new i32[n];
                i32 x = 12345;
                loop (i32 i = 0..n)
                {
                    x = x * 1103515245 + 12345;
                    numbers[i] = x >> 8 & 0xFFFFFF;
                }
                Array.Sort(numbers, (a, b) => a < b ? 1 : a > b ? -1 : 0);
                return numbers[n / 2] + numbers[0] % 1000;
            }

            public Pipeline(i32 n) : i32
            {
                List<i32> xs = new List<i32>(n);
                loop (i32 i = 0..n)
                    xs.Add(i);
                List<i32> kept = xs.Where(x => x % 3 != 0);
                List<i64> squares = kept.Select(x => (i64)x * x % 1000);
                i64 total = squares.Aggregate((i64)0, (sum, x) => sum + x);
                return (i32)(total % 1000000) + kept.Count;
            }

            public Closures(i32 n) : i32
            {
                i32 k = 3;
                func<i32, i32> : i32 step = (acc, i) => acc + (i ^ k);
                i32 acc = 0;
                loop (i32 i = 0..n)
                    acc = step(acc, i);
                return acc;
            }

            public interface IStep
            {
                Apply(i64 x) : i64;
            }

            public class AddStep : IStep
            {
                i64 k;
                public:
                    new(i64 k) { this.k = k; }
                    Apply(i64 x) : i64 => x + k;
            }

            public class MulStep : IStep
            {
                public:
                    Apply(i64 x) : i64 => x * 3 % 1000003;
            }

            public class XorStep : IStep
            {
                public:
                    Apply(i64 x) : i64 => x ^ 0x5555;
            }

            public value HalfStep : IStep
            {
                i64 d;
                public:
                    new(i64 d) { this.d = d; }
                    Apply(i64 x) : i64 => x / 2 + d;
            }

            /// Calls through interface values of four types (three classes, one boxed value), mixed.
            public IfaceCalls(i32 n) : i32
            {
                List<IStep> steps = new List<IStep>();
                steps.Add(new AddStep(7));
                steps.Add(new MulStep());
                steps.Add(new XorStep());
                steps.Add(new HalfStep(11));
                i64 x = 1;
                loop (i32 i = 0..n)
                    x = steps[i & 3].Apply(x);
                return (i32)(x % 1000000);
            }

            /// Integers boxed into a List<any>, then unboxed and summed.
            public BoxSum(i32 n) : i32
            {
                List<any> items = new List<any>(n);
                loop (i32 i = 0..n)
                    items.Add(i);
                i64 total = 0;
                loop (any item in items)
                    total += (i32)item;
                return (i32)(total % 1000000007);
            }

            public TextOps(i32 n) : i32
            {
                str line = "the quick brown fox jumps over the lazy dog";
                i32 total = 0;
                loop (i32 i = 0..n)
                {
                    str s = line.Replace("o", "0");
                    total += s.IndexOf("lazy") + s.ToUpper().Length + s.Split(' ').Count + (s.Contains("f0x") ? 1 : 0);
                }
                return total;
            }

            public Print() : i32
            {
                Console.WriteLine("hello, " + "world");
                Console.WriteLine(42);
                Console.WriteLine(2.5);
                Console.WriteLine(true);
                Console.Write("n=");
                Console.WriteLine(-7);
                Console.WriteLine("pi is about " + 3.25 + ", " + (1.5).ToString());
                List<str> names = new List<str>();
                names.Add("ana");
                names.Add("luis");
                Console.WriteLine(String.Join(", ", names).ToUpper());
                return 0;
            }

            public Main(i32 which, i32 n) : i32 => which switch
            {
                0 => Builder(n),
                1 => ListSum(n),
                2 => IntMap(n),
                3 => StrMap(n),
                4 => Words(n),
                5 => Cache(n),
                6 => SortInts(n),
                7 => SortStrings(n),
                8 => Utf8Round(n),
                10 => FileLines(n),
                11 => MathLoop(n),
                12 => SortBy(n),
                13 => Pipeline(n),
                14 => Closures(n),
                15 => TextOps(n),
                16 => IfaceCalls(n),
                17 => BoxSum(n),
                18 => BinaryRound(n),
                19 => StreamLines(n),
                _ => Print(),
            };
        }
        """;

    /// <summary>The same programs for .NET: the reference for results and speed.</summary>
    private static class Reference
    {
        public static int Builder(int n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
                sb.Append(i);
            return sb.Length;
        }

        public static int ListSum(int n)
        {
            var list = new List<int>();
            for (int i = 0; i < n; i++)
                list.Add(i);
            int total = 0;
            foreach (int x in list)
                total ^= x;
            return total + list.Count;
        }

        public static int IntMap(int n)
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
                map[i * 7] = i;
            int found = 0;
            for (int i = 0; i < n * 2; i++)
            {
                if (map.ContainsKey(i))
                    found++;
            }
            return found + map[7];
        }

        public static int StrMap(int n)
        {
            var map = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
                map[i.ToString()] = i;
            int total = 0;
            for (int i = 0; i < n; i++)
                total += map[i.ToString()] & 1;
            return total + map.Count;
        }

        public static int Cache(int n)
        {
            var map = new Dictionary<string, int>();
            for (int i = 0; i < 500000; i++)
                map[i.ToString()] = i;
            int total = 0;
            for (int i = 0; i < n; i++)
                total += map.GetValueOrDefault((i % 600000).ToString()) & 1;
            return total;
        }

        public static int SortInts(int n)
        {
            var numbers = new int[n];
            int x = 12345;
            for (int i = 0; i < n; i++)
            {
                x = unchecked(x * 1103515245 + 12345);
                numbers[i] = x >> 8 & 0xFFFFFF;
            }
            System.Array.Sort(numbers);
            return numbers[n / 2] + numbers[n - 1] % 1000;
        }

        public static int SortBy(int n)
        {
            var numbers = new int[n];
            int x = 12345;
            for (int i = 0; i < n; i++)
            {
                x = unchecked(x * 1103515245 + 12345);
                numbers[i] = x >> 8 & 0xFFFFFF;
            }
            System.Array.Sort(numbers, (a, b) => a < b ? 1 : a > b ? -1 : 0);
            return numbers[n / 2] + numbers[0] % 1000;
        }

        public static int Pipeline(int n)
        {
            var xs = new List<int>(n);
            for (int i = 0; i < n; i++)
                xs.Add(i);
            var kept = xs.Where(x => x % 3 != 0).ToList();
            var squares = kept.Select(x => (long)x * x % 1000).ToList();
            long total = squares.Aggregate(0L, (sum, x) => sum + x);
            return (int)(total % 1000000) + kept.Count;
        }

        public static int Closures(int n)
        {
            int k = 3;
            Func<int, int, int> step = (acc, i) => acc + (i ^ k);
            int acc = 0;
            for (int i = 0; i < n; i++)
                acc = unchecked(step(acc, i));
            return acc;
        }

        private interface IStep
        {
            long Apply(long x);
        }

        private sealed class AddStep(long k) : IStep
        {
            public long Apply(long x) => x + k;
        }

        private sealed class MulStep : IStep
        {
            public long Apply(long x) => x * 3 % 1000003;
        }

        private sealed class XorStep : IStep
        {
            public long Apply(long x) => x ^ 0x5555;
        }

        private readonly struct HalfStep(long d) : IStep
        {
            public long Apply(long x) => x / 2 + d;
        }

        public static int IfaceCalls(int n)
        {
            var steps = new List<IStep> { new AddStep(7), new MulStep(), new XorStep(), new HalfStep(11) };
            long x = 1;
            for (int i = 0; i < n; i++)
                x = steps[i & 3].Apply(x);
            return (int)(x % 1000000);
        }

        public static int BoxSum(int n)
        {
            var items = new List<object>(n);
            for (int i = 0; i < n; i++)
                items.Add(i);
            long total = 0;
            foreach (object item in items)
                total += (int)item;
            return (int)(total % 1000000007);
        }


        public static int TextOps(int n)
        {
            string line = "the quick brown fox jumps over the lazy dog";
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                string s = line.Replace("o", "0", StringComparison.Ordinal);
                total += s.IndexOf("lazy", StringComparison.Ordinal) + s.ToUpperInvariant().Length + s.Split(' ').Length + (s.Contains("f0x", StringComparison.Ordinal) ? 1 : 0);
            }
            return total;
        }

        public static int SortStrings(int n)
        {
            var words = new List<string>(n);
            for (int i = 0; i < n; i++)
                words.Add(unchecked(i * 7919 % n).ToString());
            words.Sort(StringComparer.Ordinal);
            return words[0].Length + words[n - 1].Length * 100 + words[n / 2].Length * 10000;
        }

        public static int Utf8Round(int n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
                sb.Append(i % 7 == 0 ? (char)233 : (char)(97 + i % 26));
            string text = sb.ToString();
            int total = 0;
            for (int round = 0; round < 10; round++)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                // A str is UTF-8: its Length is in bytes (here, the same text's UTF-8 size).
                total += bytes.Length + System.Text.Encoding.UTF8.GetByteCount(System.Text.Encoding.UTF8.GetString(bytes));
            }
            return total;
        }

        public static int FileLines(int n)
        {
            string file = Path.Combine(Path.GetTempPath(), "larva-core-bench-net.txt");
            using (var writer = new StreamWriter(file))
            {
                for (int i = 0; i < n; i++)
                    writer.Write("line " + i + "\n");
            }
            int total = 0;
            foreach (string line in File.ReadAllLines(file))
                total += line.Length;
            File.Delete(file);
            return total;
        }

        public static int BinaryRound(int n)
        {
            var memory = new MemoryStream();
            var writer = new BinaryWriter(memory);
            for (int i = 0; i < n; i++)
            {
                writer.Write(i);
                writer.Write(i * 0.5);
            }
            memory.Position = 0;
            var reader = new BinaryReader(memory);
            long total = 0;
            for (int i = 0; i < n; i++)
                total += reader.ReadInt32() + (long)reader.ReadDouble();
            return (int)(total % 1000000007);
        }

        public static int StreamLines(int n)
        {
            string file = Path.Combine(Path.GetTempPath(), "larva-core-stream-net.txt");
            using (var writer = new StreamWriter(file))
            {
                for (int i = 0; i < n; i++)
                {
                    writer.Write("line ");
                    writer.Write(i);
                    writer.Write('\n');
                }
            }
            int total = 0;
            using (var reader = new StreamReader(file))
            {
                while (reader.ReadLine() is { } line)
                    total += line.Length;
            }
            File.Delete(file);
            return total;
        }

        public static int MathLoop(int n)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += Math.Sqrt(i) + Math.Floor(i * 0.5);
            return (int)((long)sum % 1000000);
        }

        public static int Words(int n)
        {
            string text = "the quick brown fox jumps over the lazy dog";
            var counts = new Dictionary<string, int>();
            for (int round = 0; round < n; round++)
            {
                foreach (string word in text.Split(' '))
                    counts[word] = counts.GetValueOrDefault(word) + 1;
            }
            return counts["the"] + counts.Count;
        }
    }

    private void Executables(List<SourceText> library)
    {
        Assert.SkipWhen(Larva.Native.Toolchain.Find() is null, "no MSVC toolchain: executables not built");
        var watch = Stopwatch.StartNew();
        var compiled = AntrilCompiler.Compile([.. library, new SourceText(BenchSource, "bench.ant")], "CoreBench");
        Check("the benchmark source compiles", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(6)));
        if (!compiled.Success)
            return;
        var lowered = HirLowerer.Lower(compiled.Assembly!, new LoweringOptions { StaticConstructorsFromEntry = true });
        Check("and lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(4)));
        if (!lowered.Success)
            return;
        if (Environment.GetEnvironmentVariable("LARVA_BENCH_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllText(Path.Combine(dump, "core.ir.txt"), Larva.IR.Text.IrPrinter.Print(lowered.Module!));
            var optimized = LlvmBackend.Compile(lowered.Module!, new LlvmOptions { OptimizationLevel = 3 });
            if (optimized.Success)
            {
                File.WriteAllText(Path.Combine(dump, "core.ll"), optimized.Program!.IrText);
                File.WriteAllText(Path.Combine(dump, "core.s"), optimized.Program!.Assembly());
                optimized.Program!.Dispose();
            }
        }
        var objectFile = Larva.IR.Compiler.CompileObject(lowered.Module!);
        Check("its IR checks", objectFile.Success, string.Join("; ", objectFile.TypeErrors.Concat<object>(objectFile.Diagnostics).Take(4)));
        if (!objectFile.Success)
            return;
        var linked = Larva.IR.Linking.Linker.Link([objectFile.Object!], new Larva.IR.Linking.LinkOptions { Entry = "CoreBench.$Module.Main", LowerToIl = false });
        string folder = TestEnvironment.TempFolder("core-bench");
        Directory.CreateDirectory(folder);
        var exes = new List<(string Name, string Path)>();
        foreach (var (name, backend, level) in new[] { ("LLVM -O3", Larva.Driver.Backend.Llvm, 3), ("x64", Larva.Driver.Backend.X64, 0) })
        {
            string exe = Path.Combine(folder, $"core-{backend}.exe".ToLowerInvariant());
            var (ok, message) = Larva.Driver.ExecutableBuilder.Build(linked, exe, new Larva.Driver.ExecutableOptions { Backend = backend, OptimizationLevel = level });
            Check($"it builds an executable ({name})", ok, message);
            if (ok)
                exes.Add((name, exe));
        }
        const string printed = "hello, world\n42\n2.5\ntrue\nn=-7\npi is about 3.25, 1.5\nANA, LUIS\n0";
        foreach (var (name, exe) in exes)
        {
            var (output, _) = Benchmarks.Execute(exe, 9, 0);
            output = output.Replace("\r\n", "\n");
            Check($"Console output ({name})", output == printed, output.Replace("\n", "\\n"));
        }
        (string Name, int N, Func<int, int> Reference)[] benchmarks =
        [
            ("Builder", 5_000_000, Reference.Builder),
            ("ListSum", 20_000_000, Reference.ListSum),
            ("IntMap", 2_000_000, Reference.IntMap),
            ("StrMap", 1_000_000, Reference.StrMap),
            ("Words", 200_000, Reference.Words),
            ("Cache", 5_000_000, Reference.Cache),
            ("SortInts", 2_000_000, Reference.SortInts),
            ("SortStrings", 300_000, Reference.SortStrings),
            ("Utf8Round", 1_000_000, Reference.Utf8Round),
            ("_", 0, _ => 0),
            ("FileLines", 1_000_000, Reference.FileLines),
            ("MathLoop", 10_000_000, Reference.MathLoop),
            ("SortBy", 2_000_000, Reference.SortBy),
            ("Pipeline", 10_000_000, Reference.Pipeline),
            ("Closures", 100_000_000, Reference.Closures),
            ("TextOps", 1_000_000, Reference.TextOps),
            ("IfaceCalls", 100_000_000, Reference.IfaceCalls),
            ("BoxSum", 10_000_000, Reference.BoxSum),
            ("BinaryRound", 5_000_000, Reference.BinaryRound),
            ("StreamLines", 2_000_000, Reference.StreamLines),
        ];
        for (int which = 0; which < benchmarks.Length; which++)
        {
            var (name, n, reference) = benchmarks[which];
            if (name == "_")
                continue; // 9: the program's printing (checked above)
            int expected = reference(n);
            var cells = new List<string>();
            bool same = true;
            string got = "";
            foreach (var (_, exe) in exes)
            {
                var (_, baseline) = Benchmarks.Execute(exe, which, 0);
                var (output, ms) = Benchmarks.Execute(exe, which, n);
                same &= output == expected.ToString();
                got += output + " ";
                cells.Add($"{Math.Max(0, ms - baseline),9:0} ms");
            }
            var timer = Stopwatch.StartNew();
            reference(n);
            double netMs = timer.Elapsed.TotalMilliseconds;
            Check($"{name}: the executables give {expected}", same, got);
        }
    }
}
