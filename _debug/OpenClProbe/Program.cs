using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenCL.Net;
using Plat = OpenCL.Net.Platform;
using Dev = OpenCL.Net.Device;
using Ctx = OpenCL.Net.Context;
using CQ = OpenCL.Net.CommandQueue;
using Prog = OpenCL.Net.Program;
using Kern = OpenCL.Net.Kernel;
using Ev = OpenCL.Net.Event;

Console.WriteLine("=== OpenCL V100 Direct Probe ===");
Console.WriteLine($"Process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine();

// Step 1: Enumerate all platforms and devices
Plat[] platforms = Cl.GetPlatformIDs(out ErrorCode err);
Console.WriteLine($"GetPlatformIDs: {err}, count={platforms.Length}");

Plat nvidiaPlatform = default;
Dev nvidiaDevice = default;
bool foundNvidia = false;

foreach (Plat plat in platforms)
{
    string platName = Cl.GetPlatformInfo(plat, PlatformInfo.Name, out _).ToString().Trim();
    string platVendor = Cl.GetPlatformInfo(plat, PlatformInfo.Vendor, out _).ToString().Trim();
    string platVersion = Cl.GetPlatformInfo(plat, PlatformInfo.Version, out _).ToString().Trim();
    IntPtr platHandle = Unsafe.As<Plat, IntPtr>(ref plat);
    Console.WriteLine($"\nPlatform: {platName} ({platVendor})  Version: {platVersion}  Handle: 0x{platHandle:X}");

    Dev[] devices = Cl.GetDeviceIDs(plat, DeviceType.Gpu, out ErrorCode devErr);
    if (devErr != ErrorCode.Success || devices.Length == 0)
    {
        Console.WriteLine($"  No GPU devices ({devErr})");
        continue;
    }

    foreach (Dev dev in devices)
    {
        string devName = Cl.GetDeviceInfo(dev, DeviceInfo.Name, out _).ToString().Trim();
        string devVendor = Cl.GetDeviceInfo(dev, DeviceInfo.Vendor, out _).ToString().Trim();
        uint cu = Unsafe.As<InfoBuffer, uint>(ref Unsafe.AsRef(Cl.GetDeviceInfo(dev, DeviceInfo.MaxComputeUnits, out _)));
        ulong mem = Unsafe.As<InfoBuffer, ulong>(ref Unsafe.AsRef(Cl.GetDeviceInfo(dev, DeviceInfo.GlobalMemSize, out _)));
        IntPtr devHandle = Unsafe.As<Dev, IntPtr>(ref dev);
        Console.WriteLine($"  Device: {devName} ({devVendor})  CU={cu}  Mem={mem / (1024 * 1024 * 1024)} GB  Handle: 0x{devHandle:X}");

        if (platName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            devVendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            nvidiaPlatform = plat;
            nvidiaDevice = dev;
            foundNvidia = true;
        }
    }
}

if (!foundNvidia)
{
    Console.WriteLine("\n*** No NVIDIA device found! ***");
    return;
}

Console.WriteLine($"\n--- Testing NVIDIA device ---");

// Step 2: Create context WITHOUT platform property (the old broken way)
Console.WriteLine("\n[Test A] CreateContext with NULL properties (old code):");
try
{
    Ctx ctxA = Cl.CreateContext(null!, 1, [nvidiaDevice], null!, IntPtr.Zero, out err);
    Console.WriteLine($"  CreateContext: {err}");
    if (err == ErrorCode.Success)
    {
        TestKernelExecution(ctxA, nvidiaDevice, "Test-A");
        Cl.ReleaseContext(ctxA);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  EXCEPTION: {ex.Message}");
}

// Step 3: Create context WITH platform property (the fix)
Console.WriteLine("\n[Test B] CreateContext with explicit NVIDIA platform property:");
try
{
    IntPtr platH = Unsafe.As<Plat, IntPtr>(ref nvidiaPlatform);
    Console.WriteLine($"  Platform handle: 0x{platH:X}");
    ContextProperty[] props = [
        new ContextProperty(ContextProperties.Platform, platH),
        ContextProperty.Zero,
    ];
    Ctx ctxB = Cl.CreateContext(props, 1, [nvidiaDevice], null!, IntPtr.Zero, out err);
    Console.WriteLine($"  CreateContext: {err}");
    if (err == ErrorCode.Success)
    {
        TestKernelExecution(ctxB, nvidiaDevice, "Test-B");
        Cl.ReleaseContext(ctxB);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  EXCEPTION: {ex.Message}");
}

// Step 4: Create context using the string/wildcard overload
Console.WriteLine("\n[Test C] CreateContext with 'NVIDIA' wildcard string:");
try
{
    Ctx ctxC = Cl.CreateContext("NVIDIA", DeviceType.Gpu, out err);
    Console.WriteLine($"  CreateContext: {err}");
    if (err == ErrorCode.Success)
    {
        // Need to figure out which device it chose — query context devices
        TestKernelExecution(ctxC, nvidiaDevice, "Test-C");
        Cl.ReleaseContext(ctxC);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  EXCEPTION: {ex.Message}");
}

Console.WriteLine("\n=== Probe complete ===");

static void TestKernelExecution(Ctx context, Dev device, string label)
{
    const string kernelSrc = @"
__kernel void add_one(__global float* data, int count)
{
    int gid = get_global_id(0);
    if (gid < count) data[gid] = data[gid] + 1.0f;
}
";

    try
    {
        CQ queue = Cl.CreateCommandQueue(context, device, CommandQueueProperties.ProfilingEnable, out ErrorCode err);
        if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] CreateCommandQueue: {err}"); return; }

        Prog program = Cl.CreateProgramWithSource(context, 1, [kernelSrc], null!, out err);
        if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] CreateProgramWithSource: {err}"); Cl.ReleaseCommandQueue(queue); return; }

        err = Cl.BuildProgram(program, 1, [device], "-cl-fast-relaxed-math", null!, IntPtr.Zero);
        if (err != ErrorCode.Success)
        {
            string log = Cl.GetProgramBuildInfo(program, device, ProgramBuildInfo.Log, out _).ToString();
            Console.WriteLine($"  [{label}] BuildProgram: {err}  Log: {log}");
            Cl.ReleaseProgram(program);
            Cl.ReleaseCommandQueue(queue);
            return;
        }
        Console.WriteLine($"  [{label}] Kernel compiled OK");

        Kern kernel = Cl.CreateKernel(program, "add_one", out err);
        if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] CreateKernel: {err}"); return; }

        // Create input data
        const int N = 1024 * 1024; // 1M elements
        float[] hostData = new float[N];
        for (int i = 0; i < N; i++) hostData[i] = i;

        IMem<float> buf = Cl.CreateBuffer<float>(context, MemFlags.ReadWrite | MemFlags.CopyHostPtr, hostData, out err);
        if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] CreateBuffer: {err}"); return; }
        Console.WriteLine($"  [{label}] Buffer created ({N} floats, {N * 4 / 1024} KB)");

        // Set kernel args
        err = Cl.SetKernelArg(kernel, 0, buf);
        if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] SetKernelArg(0): {err}"); return; }
        err = Cl.SetKernelArg(kernel, 1, N);
        if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] SetKernelArg(1): {err}"); return; }

        // Execute — 3 warmups + 10 timed
        for (int w = 0; w < 3; w++)
        {
            err = Cl.EnqueueNDRangeKernel(queue, kernel, 1, null!, [new IntPtr(N)], null!, 0, null!, out Ev _wev);
            if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] Warmup EnqueueNDRangeKernel: {err}"); return; }
        }
        Cl.Finish(queue);
        Console.WriteLine($"  [{label}] Warmup OK");

        // Timed runs
        const int RUNS = 10;
        double totalKernelNs = 0;
        Stopwatch sw = Stopwatch.StartNew();
        for (int r = 0; r < RUNS; r++)
        {
            err = Cl.EnqueueNDRangeKernel(queue, kernel, 1, null!, [new IntPtr(N)], null!, 0, null!, out Ev ev);
            if (err != ErrorCode.Success) { Console.WriteLine($"  [{label}] EnqueueNDRangeKernel: {err}"); return; }
            Cl.Finish(queue);

            // Query profiling
            InfoBuffer startBuf = Cl.GetEventProfilingInfo(ev, ProfilingInfo.Start, out ErrorCode sErr);
            InfoBuffer endBuf = Cl.GetEventProfilingInfo(ev, ProfilingInfo.End, out ErrorCode eErr);
            if (sErr == ErrorCode.Success && eErr == ErrorCode.Success)
            {
                ulong startNs = startBuf.CastTo<ulong>();
                ulong endNs = endBuf.CastTo<ulong>();
                totalKernelNs += (endNs - startNs);
            }
            else
            {
                Console.WriteLine($"  [{label}] Profiling failed: start={sErr}, end={eErr}");
            }
            Cl.ReleaseEvent(ev);
        }
        sw.Stop();

        // Read back and verify
        float[] result = new float[N];
        Cl.EnqueueReadBuffer(queue, buf, Bool.True, IntPtr.Zero, N, result, 0, null!, out Ev _rev);
        Cl.Finish(queue);

        bool correct = true;
        int wrongCount = 0;
        for (int i = 0; i < Math.Min(N, 100); i++)
        {
            // After 3 warmups + 10 timed = 13 increments
            float expected = i + 13.0f;
            if (Math.Abs(result[i] - expected) > 0.01f)
            {
                if (wrongCount < 5) Console.WriteLine($"  [{label}] WRONG: result[{i}]={result[i]}, expected={expected}");
                correct = false;
                wrongCount++;
            }
        }

        double avgHostMs = sw.Elapsed.TotalMilliseconds / RUNS;
        double avgKernelMs = totalKernelNs / RUNS / 1_000_000.0;

        Console.WriteLine($"  [{label}] Results: {(correct ? "CORRECT" : $"WRONG ({wrongCount} mismatches)")}");
        Console.WriteLine($"  [{label}] Host avg: {avgHostMs:F3} ms,  Kernel avg: {avgKernelMs:F3} ms  ({RUNS} runs)");

        Cl.ReleaseMemObject(buf);
        Cl.ReleaseKernel(kernel);
        Cl.ReleaseProgram(program);
        Cl.ReleaseCommandQueue(queue);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{label}] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"  {ex.StackTrace}");
    }
}
