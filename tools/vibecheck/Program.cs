// Feeds a synthetic timeline through LightingSync+SoundEnvelope and prints the Vibe step for
// every 20 ms tick, so the step machine can be checked without a keyboard or a tester.
//
//   dotnet build app\Nitrous.csproj -c Release
//   dotnet run --project tools\vibecheck -c Release [-- <path-to-Nitrous.dll>]

using System.Reflection;

const int Rate = 48000;
const int Block = 960;      // 20 ms, the brightness path's ticket
const float Dt = 0.02f;

var dll = args.Length > 0 ? args[0] : FindBuiltDll();

if (!File.Exists(dll))
{
    Console.Error.WriteLine($"Nitrous.dll not found at {dll}");
    Console.Error.WriteLine("Build it first: dotnet build app\\Nitrous.csproj -c Release");
    return 1;
}

var asm = Assembly.LoadFrom(dll);
var type = asm.GetType("Nitrous.Managers.LightingSync+SoundEnvelope", throwOnError: true)!;
var envelope = Activator.CreateInstance(type, nonPublic: true)!;

type.GetMethod("Configure")!.Invoke(envelope, new object[] { Rate });

var analyze = type.GetMethod("Analyze", new[]
{
    typeof(float[]), typeof(float).MakeByRefType(), typeof(float).MakeByRefType(),
    typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType(),
    typeof(float).MakeByRefType()
}) ?? throw new MissingMethodException("six-band Analyze not found");

var vibe = type.GetMethod("Vibe") ?? throw new MissingMethodException("Vibe not found");

// 3 s timeline: a 500 Hz pad from 0.2 s, a 60 Hz kick at 0.5 s, a noise hat at 1.0 s,
// silence from 1.6 s.
float[] Build(int index)
{
    var t = index * Dt;
    var block = new float[Block];
    if (t < 0.2f || t > 1.6f) return block;

    for (var i = 0; i < Block; i++)
    {
        var time = t + i / (float)Rate;
        var sample = 0.06f * MathF.Sin(2 * MathF.PI * 500f * time);

        var kickAge = time - 0.5f;
        if (kickAge is >= 0f and < 0.08f)
            sample += 0.9f * MathF.Sin(2 * MathF.PI * 60f * kickAge) * MathF.Exp(-kickAge * 28f);

        var hatAge = time - 1.0f;
        if (hatAge is >= 0f and < 0.02f)
            sample += 0.2f * (float)(Random.Shared.NextDouble() * 2 - 1);

        block[i] = sample;
    }

    return block;
}

var bands = new object[7];
Console.WriteLine("   t(ms)  vibe    bass    high    vl      vh");
for (var i = 0; i < 150; i++)
{
    bands[0] = Build(i);
    for (var b = 1; b < 7; b++) bands[b] = 0f;
    analyze.Invoke(envelope, bands);

    var step = (int)vibe.Invoke(envelope, new object[]
    {
        bands[1], bands[2], bands[3], bands[4], bands[5], bands[6], 0.9f, Dt
    })!;

    Console.WriteLine(
        $"{i * 20,7}  {step,4}  {(float)bands[1]!,6:0.000}  {(float)bands[4]!,6:0.000}  " +
        $"{(float)bands[5]!,6:0.000}  {(float)bands[6]!,6:0.000}");
}

return 0;

// Walk up from the harness output folder until the repository root is found, so the default
// works whatever RID or configuration folder dotnet picks.
static string FindBuiltDll()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "app", "Nitrous.csproj")))
            return Path.Combine(dir.FullName, "bin", "Release", "net10.0-windows", "Nitrous.dll");

        dir = dir.Parent;
    }

    return Path.Combine("bin", "Release", "net10.0-windows", "Nitrous.dll");
}
