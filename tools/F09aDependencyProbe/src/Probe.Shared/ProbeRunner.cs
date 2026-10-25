// P-F09a 受控验证探针 —— 共享实现（由 Probe.Baseline / Probe.Fixed 两个变体工程链接编译）。
//
// 目的：在同一台机器（双 NVIDIA RTX 3080 / 3080 Ti）上，用"注入故障"的方式比较
//   候选 A：LHM 0.9.6（当前生产依赖，传递 DiskInfoToolkit 1.1.2）
//   候选 B：LHM 0.9.7-preNNN（含 PR #2539 NVML 句柄生命周期修复，传递 DiskInfoToolkit 2.2.x）
// 的可观测行为差异，而不是靠版本号推断。
//
// 场景：
//   fingerprint          —— 反射读取已加载程序集的结构指纹（句柄缓存字段 / SyncRoot / GetNvmlDevice）
//   nvml-smoke           —— 真机冒烟：枚举 NVIDIA GPU、逐轮更新、读功耗传感器、按 PCI 身份解析 NVML 句柄
//   nvml-invalidate      —— 故障注入：模拟 NVML 句柄/驱动不可用，观察功耗读数是"冻结成旧值"还是"重新取句柄后刷新"
//   disk-thread-fault    —— 故障注入：往库内部状态里塞 null，触发库自带后台线程，观察是否把进程带崩
//   disk-callback-fault  —— 故障注入：注册一个会抛异常的设备变更回调，观察异常落在谁身上
//
// 输出：每行 "PROBE|key=value"，便于脚本汇总。

using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace F09a.Probe;

internal static class ProbeRunner
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Run(string[] args)
    {
        string scenario = args.Length > 0 ? args[0] : "fingerprint";
        int cycles = 3;

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--cycles", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(args[i + 1], out cycles);
            }
        }

        Console.OutputEncoding = Encoding.UTF8;
        Emit("scenario", scenario);
        Emit("lhmAssembly", typeof(Computer).Assembly.GetName().Version?.ToString() ?? "?");
        Emit("lhmInformationalVersion", ReadInformationalVersion(typeof(Computer).Assembly));
        Emit("lhmLocation", typeof(Computer).Assembly.Location);

        try
        {
            switch (scenario)
            {
                case "fingerprint": Fingerprint(); break;
                case "nvml-smoke": NvmlSmoke(cycles); break;
                case "nvml-stale-handle": NvmlStaleHandle(cycles); break;
                case "nvml-invalidate": NvmlInvalidate(cycles); break;
                case "disk-thread-fault": DiskThreadFault(); break;
                case "disk-callback-fault": DiskCallbackFault(); break;
                default:
                    Emit("result", "unknown-scenario");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Emit("probeException", ex.GetType().FullName + ": " + ex.Message);
            return 1;
        }

        Emit("result", "completed");
        return 0;
    }

    // ---------------------------------------------------------------- 指纹

    private static void Fingerprint()
    {
        Assembly lhm = typeof(Computer).Assembly;

        Type nvml = lhm.GetType("LibreHardwareMonitor.Interop.NvidiaML");
        Type gpu = lhm.GetType("LibreHardwareMonitor.Hardware.Gpu.NvidiaGpu");

        Emit("nvmlTypeFound", nvml != null);
        Emit("nvmlSyncRootProperty", nvml?.GetProperty("SyncRoot", AnyStatic) != null);
        Emit("nvidiaGpuTypeFound", gpu != null);
        Emit("nvidiaGpuCachedNvmlDeviceField", gpu?.GetField("_nvmlDevice", BindingFlags.Instance | BindingFlags.NonPublic) != null);
        Emit("nvidiaGpuCachedFieldIsReadonly",
            gpu?.GetField("_nvmlDevice", BindingFlags.Instance | BindingFlags.NonPublic)?.IsInitOnly);
        Emit("nvidiaGpuPciBusIdField", gpu?.GetField("_nvmlPciBusId", BindingFlags.Instance | BindingFlags.NonPublic) != null);
        Emit("nvidiaGpuGetNvmlDeviceMethod", gpu?.GetMethod("GetNvmlDevice", BindingFlags.Instance | BindingFlags.NonPublic) != null);

        // DiskInfoToolkit：两个大版本 API 面完全不同，这里按类型是否存在来判别。
        Assembly dit = FindDiskInfoToolkitAssembly();
        Emit("diskinfotoolkitAssembly", dit?.GetName().Version?.ToString() ?? "(not loaded)");
        if (dit != null)
        {
            Emit("ditHasStorageManager", dit.GetType("DiskInfoToolkit.StorageManager") != null);
            Emit("ditHasStaticStorage", dit.GetType("DiskInfoToolkit.Storage") != null);
            Emit("ditHasExcludeUsbDevices",
                dit.GetType("DiskInfoToolkit.Storage")?.GetProperty("ExcludeUsbDevices", AnyStatic) != null);
            Emit("ditHasStartMonitoring",
                dit.GetType("DiskInfoToolkit.Storage")?.GetMethod("StartMonitoring", AnyStatic) != null);
            Emit("ditHasStopMonitoring",
                dit.GetType("DiskInfoToolkit.Storage")?.GetMethod("StopMonitoring", AnyStatic) != null);
        }
    }

    // ------------------------------------------------------------ NVML 冒烟

    private static void NvmlSmoke(int cycles)
    {
        Computer computer = OpenComputer();
        try
        {
            List<IHardware> nvidia = NvidiaHardware(computer);

            Emit("nvidiaGpuCount", nvidia.Count);
            foreach (IHardware hw in nvidia)
            {
                Emit("gpu", hw.Name);
            }

            Type nvml = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvidiaML");
            MethodInfo byPci = nvml?.GetMethod("NvmlDeviceGetHandleByPciBusId", AnyStatic);
            MethodInfo byIndex = nvml?.GetMethod("NvmlDeviceGetHandleByIndex", AnyStatic);
            MethodInfo powerUsage = nvml?.GetMethod("NvmlDeviceGetPowerUsage", AnyStatic);

            foreach (IHardware hw in nvidia)
            {
                string busId = ResolveBusId(hw, nvml);
                Emit("gpuBusId", hw.Name + "=" + (busId ?? "(null)"));

                if (byPci != null && !string.IsNullOrEmpty(busId))
                {
                    object handle = byPci.Invoke(null, new object[] { busId });
                    Emit("nvmlHandleByPciBusId", hw.Name + "=" + (handle != null));
                    if (handle != null && powerUsage != null)
                    {
                        object watts = powerUsage.Invoke(null, new[] { handle });
                        Emit("nvmlPowerUsageByBusId", hw.Name + "=" + (watts ?? "(null)"));
                    }
                }
            }

            // 身份绑定对照：按 PCI 身份取到的句柄应与"按枚举序号"取到的句柄一一对应且互不相同。
            if (byIndex != null)
            {
                List<string> indexHandles = new();
                for (int i = 0; i < 8; i++)
                {
                    object handle = byIndex.Invoke(null, new object[] { i });
                    if (handle != null)
                    {
                        indexHandles.Add(i + ":" + HandleValue(handle));
                    }
                }

                Emit("nvmlHandlesByIndex", string.Join(",", indexHandles));
            }

            // 失效句柄不应被"按序号回退"顶替：伪造一个不存在的 PCI 身份必须拿不到句柄。
            if (byPci != null)
            {
                object bogus = byPci.Invoke(null, new object[] { "0000:FF:00.0" });
                Emit("nvmlHandleForBogusPciBusId", bogus != null);
            }

            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                UpdateAll(computer);
                foreach (IHardware hw in nvidia)
                {
                    ISensor power = hw.Sensors.FirstOrDefault(s => s.Name == "GPU Package");
                    Emit("cycle", cycle + " " + hw.Name + " GPU.Package=" + Format(power?.Value));
                }
            }
        }
        finally
        {
            computer.Close();
        }
    }

    // ------------------------------------------------- NVML 句柄失效注入

    private static void NvmlInvalidate(int cycles)
    {
        Computer computer = OpenComputer();
        try
        {
            List<IHardware> nvidia = NvidiaHardware(computer);
            Emit("nvidiaGpuCount", nvidia.Count);

            Type nvml = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvidiaML");
            PropertyInfo available = nvml?.GetProperty("IsAvailable", AnyStatic);
            MethodInfo getter = available?.GetGetMethod();
            MethodInfo setter = available?.GetSetMethod(true);

            // 基线：先采样，得到"健康"读数。
            UpdateAll(computer);
            List<float?> before = nvidia.Select(h => ReadPower(h)).ToList();

            Emit("nvmlAvailableBefore", getter?.Invoke(null, null));

            // 故障注入：把 NVML 置为不可用（等价于驱动句柄失效/驱动重置后的库侧状态）。
            setter?.Invoke(null, new object[] { false });
            Emit("nvmlAvailableAfterInject", getter?.Invoke(null, null));

            List<string> trace = new();
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                UpdateAll(computer);
                for (int i = 0; i < nvidia.Count; i++)
                {
                    float? now = ReadPower(nvidia[i]);
                    trace.Add("c" + cycle + ":" + nvidia[i].Name + "=" + Format(now));
                }
            }

            Emit("nvmlTraceAfterInvalidate", string.Join(" | ", trace));

            bool anyNull = trace.Any(t => t.EndsWith("=(null)", StringComparison.Ordinal));
            bool anyChanged = false;
            for (int i = 0; i < nvidia.Count; i++)
            {
                float? now = ReadPower(nvidia[i]);
                if (before[i].HasValue != now.HasValue)
                {
                    anyChanged = true;
                }
                else if (before[i].HasValue && now.HasValue && Math.Abs(before[i].Value - now.Value) > 0.001f)
                {
                    anyChanged = true;
                }
            }

            Emit("powerReadoutChangedAfterInvalidate", anyChanged);
            Emit("powerReadoutWentNull", anyNull);

            // 恢复：把 NVML 重新置为可用，观察读数是否还能继续刷新。
            setter?.Invoke(null, new object[] { true });
            UpdateAll(computer);
            Emit("powerReadoutAfterRestore", string.Join(" | ", nvidia.Select(h => h.Name + "=" + Format(ReadPower(h)))));
            Emit("nvmlAvailableAfterRestore", getter?.Invoke(null, null));
        }
        finally
        {
            computer.Close();
        }
    }

    // -------------------------------------------- DiskInfoToolkit 线程故障

    private static void DiskThreadFault()
    {
        Assembly dit = FindDiskInfoToolkitAssembly();
        if (dit == null)
        {
            Emit("result", "diskinfotoolkit-not-loaded");
            return;
        }

        Type manager = dit.GetType("DiskInfoToolkit.StorageManager");
        if (manager != null)
        {
            RunLegacyThreadFault(manager);
            return;
        }

        Type storage = dit.GetType("DiskInfoToolkit.Storage");
        if (storage != null)
        {
            RunModernThreadFault(storage);
            return;
        }

        Emit("result", "unknown-disk-api");
    }

    /// <summary>1.1.2：库自带 DevicesChangedListener 线程，循环体没有任何异常边界。</summary>
    private static void RunLegacyThreadFault(Type manager)
    {
        Emit("diskApi", "legacy-StorageManager");

        FieldInfo storagesField = manager.GetField("_Storages", AnyStatic);
        object storages = storagesField?.GetValue(null);   // 触发静态构造 → 启动库自带监听线程
        Emit("legacyStoragesType", storages?.GetType().FullName);

        if (storages is IList list)
        {
            list.Add(null);                                 // 注入：设备元数据缺失（null 条目）
        }

        Emit("legacyPoisonedWithNull", storages is IList l2 && l2.Contains(null));

        TriggerLegacyListener(manager);
        Emit("legacyTriggered", true);
        Thread.Sleep(4000);

        // 能走到这里说明库线程没有把进程带崩（异常边界存在或未触发）。
        Emit("legacyProcessAlive", true);
    }

    /// <summary>2.2.x：库自带 RescanLoop 线程，循环体内的异常被库自己吞掉。</summary>
    private static void RunModernThreadFault(Type storage)
    {
        Emit("diskApi", "modern-Storage");

        PropertyInfo exclude = storage.GetProperty("ExcludeUsbDevices", AnyStatic);
        exclude?.SetValue(null, true);

        MethodInfo start = storage.GetMethod("StartMonitoring", AnyStatic);
        start?.Invoke(null, null);

        FieldInfo current = storage.GetField("_currentDisks", AnyStatic);
        if (current?.GetValue(null) is IList disks)
        {
            disks.Add(null);                                // 注入：设备元数据缺失（null 条目）
            Emit("modernPoisonedWithNull", disks.Contains(null));
        }

        TriggerModernRescan(storage);
        Emit("modernTriggered", true);
        Thread.Sleep(4000);

        Thread rescan = FindThread(storage, "Storage.RescanLoop");
        Emit("modernRescanThreadAlive", rescan?.IsAlive);
        Emit("modernProcessAlive", true);
    }

    // ------------------------------------------ DiskInfoToolkit 回调故障

    private static void DiskCallbackFault()
    {
        Assembly dit = FindDiskInfoToolkitAssembly();
        if (dit == null)
        {
            Emit("result", "diskinfotoolkit-not-loaded");
            return;
        }

        Type manager = dit.GetType("DiskInfoToolkit.StorageManager");
        if (manager != null)
        {
            Emit("diskApi", "legacy-StorageManager");

            EventInfo evt = manager.GetEvent("StoragesChanged", AnyStatic);
            MethodInfo add = evt?.GetAddMethod(true);
            Delegate handler = CreateThrowingHandler(evt?.EventHandlerType);
            add?.Invoke(null, new[] { handler });
            Emit("legacyCallbackRegistered", add != null);

            SeedFakeStorages(manager, driveNumbers: new[] { 0, 1, 2, 3, 4, 5, 6, 7 });

            TriggerLegacyListener(manager, driveLetterRemoved: true);
            Emit("legacyTriggered", true);

            // 1.1.2 的设备识别很慢（会逐个探测 PhysicalDrive0..63），轮询等待回调触发。
            for (int i = 1; i <= 20 && CallbackInvocations == 0; i++)
            {
                Thread.Sleep(1000);
                if (i % 10 == 0)
                {
                    FieldInfo storagesField = manager.GetField("_Storages", AnyStatic);
                    int count = (storagesField?.GetValue(null) as IList)?.Count ?? -1;
                    Emit("legacyWaitSeconds", i + " storages=" + count);
                }
            }

            Emit("legacyCallbackInvocations", CallbackInvocations);
            Emit("legacyProcessAlive", true);
            return;
        }

        Type storage = dit.GetType("DiskInfoToolkit.Storage");
        if (storage != null)
        {
            Emit("diskApi", "modern-Storage");

            PropertyInfo exclude = storage.GetProperty("ExcludeUsbDevices", AnyStatic);
            exclude?.SetValue(null, true);

            EventInfo evt = storage.GetEvent("DevicesChanged", AnyStatic);
            MethodInfo add = evt?.GetAddMethod(true);
            Delegate handler = CreateThrowingHandler(evt?.EventHandlerType);
            add?.Invoke(null, new[] { handler });
            Emit("modernCallbackRegistered", add != null);

            MethodInfo start = storage.GetMethod("StartMonitoring", AnyStatic);
            start?.Invoke(null, null);

            // 把库内"已缓存磁盘"清空 → 下一轮 rescan 会认为设备集合发生变化 → 一定触发回调。
            FieldInfo current = storage.GetField("_currentDisks", AnyStatic);
            if (current?.GetValue(null) is IList disks)
            {
                disks.Clear();
            }

            TriggerModernRescan(storage);
            Emit("modernTriggered", true);

            for (int i = 1; i <= 90 && CallbackInvocations == 0; i++)
            {
                Thread.Sleep(1000);
                if (i % 10 == 0)
                {
                    Emit("modernWaitSeconds", i);
                }
            }

            Thread rescan = FindThread(storage, "Storage.RescanLoop");
            Emit("modernRescanThreadAlive", rescan?.IsAlive);
            Emit("modernCallbackInvocations", CallbackInvocations);
            Emit("modernProcessAlive", true);
        }
    }

    // ------------------------------------------ NVML 句柄失效（运行时判定）

    /// <summary>
    /// 模拟"句柄失效"后的行为差异（确定性判定，不依赖功耗数值波动）：
    ///   1. 先正常采样，拿到健康读数；
    ///   2. 把功率传感器的值清空（模拟"这次采样没拿到数据"）；
    ///   3. 基线额外把缓存的 readonly _nvmlDevice 句柄置空（模拟驱动重置后句柄失效）；
    ///   4. 再跑若干轮采样。
    /// 期望：
    ///   基线（0.9.6，缓存句柄）→ 句柄被置空后 NVML 分支被整体跳过 → 传感器一直是 null（再无数据）。
    ///   修复版（无缓存句柄，每轮按 PCI 身份重取）→ 传感器重新拿到真实功耗值。
    /// </summary>
    private static void NvmlStaleHandle(int cycles)
    {
        Computer computer = OpenComputer();
        try
        {
            List<IHardware> nvidia = NvidiaHardware(computer);
            Emit("nvidiaGpuCount", nvidia.Count);

            UpdateAll(computer);
            Emit("powerBeforeInjection", string.Join(" | ", nvidia.Select(h => h.Name + "=" + Format(ReadPower(h)))));

            FieldInfo cachedHandle = typeof(Computer).Assembly
                .GetType("LibreHardwareMonitor.Hardware.Gpu.NvidiaGpu")?
                .GetField("_nvmlDevice", BindingFlags.Instance | BindingFlags.NonPublic);

            Emit("nvidiaGpuHasCachedHandleField", cachedHandle != null);

            foreach (IHardware hw in nvidia)
            {
                ISensor power = hw.Sensors.FirstOrDefault(s => s.Name == "GPU Package");
                ClearSensorValue(power);
                if (cachedHandle != null)
                {
                    cachedHandle.SetValue(hw, null);
                }
            }

            Emit("injectionApplied", "sensorValueCleared=true cachedHandleCleared=" + (cachedHandle != null));

            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                UpdateAll(computer);
                Emit("cycleAfterInjection", cycle + " " + string.Join(" | ", nvidia.Select(h => h.Name + "=" + Format(ReadPower(h)))));
            }

            bool anyAlive = nvidia.Any(h => ReadPower(h).HasValue);
            Emit("powerRecoveredAfterHandleInvalidation", anyAlive);
        }
        finally
        {
            computer.Close();
        }
    }

    // ---------------------------------------------------------------- 工具

    private static Delegate CreateThrowingHandler(Type handlerType)
    {
        // 事件委托签名各版本不同（1.1.2 是 (StoragesChangedEventArgs)，2.x 是 (object, EventArgs)），
        // 因此用 DynamicMethod 生成"先计数、再抛异常"的处理器，与签名无关。
        MethodInfo invoke = handlerType.GetMethod("Invoke")!;
        Type[] parameterTypes = invoke.GetParameters().Select(p => p.ParameterType).ToArray();

        DynamicMethod method = new DynamicMethod(
            "InjectedCallbackFault",
            invoke.ReturnType,
            parameterTypes,
            typeof(ProbeRunner).Module,
            skipVisibility: true);

        FieldInfo counter = typeof(ProbeRunner).GetField(nameof(CallbackInvocations), BindingFlags.Static | BindingFlags.NonPublic);

        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, counter);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, counter);
        il.Emit(OpCodes.Ldstr, "F09a injected callback fault");
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
        il.Emit(OpCodes.Throw);

        return method.CreateDelegate(handlerType, null);
    }

    private static int CallbackInvocations;

    private static void InjectedCallbackFault(object sender, EventArgs e)
    {
        throw new InvalidOperationException("F09a injected callback fault");
    }

    /// <summary>
    /// 往 1.1.2 的 <c>_Storages</c> 里塞几个"假设备"（DriveNumber 0..7，路径指向不存在的盘）。
    /// 目的：让库自带的"盘符移除"路径能在**不依赖真实热插拔**的情况下走到
    /// <c>StoragesChanged(Removed)</c> 事件，从而在库自己的线程上触发应用回调异常。
    /// </summary>
    private static void SeedFakeStorages(Type manager, int[] driveNumbers)
    {
        Assembly dit = manager.Assembly;
        Type deviceType = dit.GetType("DiskInfoToolkit.Internal.StorageDevice");
        Type storageType = dit.GetType("DiskInfoToolkit.Storage");
        if (deviceType == null || storageType == null)
        {
            Emit("legacySeed", "(types not found)");
            return;
        }

        ConstructorInfo ctor = storageType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 2);
        if (ctor == null)
        {
            Emit("legacySeed", "(ctor not found)");
            return;
        }

        FieldInfo storagesField = manager.GetField("_Storages", AnyStatic);
        if (storagesField?.GetValue(null) is not IList list)
        {
            Emit("legacySeed", "(_Storages not found)");
            return;
        }

        int seeded = 0;
        foreach (int driveNumber in driveNumbers)
        {
            try
            {
                object device = Activator.CreateInstance(deviceType);
                deviceType.GetProperty("PhysicalPath")?.SetValue(device, @"\\.\PhysicalDrive" + driveNumber + "ZZ");
                deviceType.GetProperty("DeviceID")?.SetValue(device, string.Empty);
                deviceType.GetProperty("HardwareID")?.SetValue(device, string.Empty);
                deviceType.GetProperty("DriveNumber")?.SetValue(device, driveNumber);

                object storage = ctor.Invoke(new object[] { "F09a fake controller", device });
                list.Add(storage);
                seeded++;
            }
            catch (Exception ex)
            {
                Emit("legacySeedError", ex.GetType().Name + ": " + ex.Message);
            }
        }

        Emit("legacySeededFakeStorages", seeded);
    }

    private static void TriggerLegacyListener(Type manager, bool driveLetterAdded = false, bool driveLetterRemoved = false)
    {
        FieldInfo queueField = manager.GetField("_ChangedStorages", AnyStatic);
        FieldInfo signalField = manager.GetField("_DevicesChangedEvent", AnyStatic);

        object queue = queueField?.GetValue(null);
        object signal = signalField?.GetValue(null);

        Type modelType = manager.Assembly.GetType("DiskInfoToolkit.Models.DeviceChangedModel");
        Type enumType = manager.Assembly.GetType("DiskInfoToolkit.Models.StorageChangeIdentifierInternal");
        object model = Activator.CreateInstance(modelType);
        PropertyInfo prop = modelType.GetProperty("StorageChangeIdentifier");
        object identifier = Enum.Parse(enumType,
            driveLetterAdded ? "Added" : driveLetterRemoved ? "Removed" : "DevicesChanged");
        prop?.SetValue(model, identifier);

        if (driveLetterAdded || driveLetterRemoved)
        {
            PropertyInfo driveLetter = modelType.GetProperty("DriveLetter");
            driveLetter?.SetValue(model, 'C');
        }

        MethodInfo enqueue = queue?.GetType().GetMethod("Enqueue");
        enqueue?.Invoke(queue, new[] { model });

        (signal as AutoResetEvent)?.Set();
    }

    private static void TriggerModernRescan(Type storage)
    {
        FieldInfo signalField = storage.GetField("RescanSignal", AnyStatic);
        object signal = signalField?.GetValue(null);
        (signal as AutoResetEvent)?.Set();
    }

    private static Thread FindThread(Type storage, string name)
    {
        FieldInfo threadField = storage.GetField("_rescanThread", AnyStatic);
        return threadField?.GetValue(null) as Thread;
    }

    private static Computer OpenComputer()
    {
        Computer computer = new Computer
        {
            IsCpuEnabled = false,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsStorageEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false
        };
        computer.Open();
        return computer;
    }

    /// <summary>与产品 <c>CollectOnce</c> 同形：逐个硬件对象调用 <c>hw.Update()</c>。</summary>
    private static void UpdateAll(Computer computer)
    {
        foreach (IHardware hw in AllHardware(computer))
        {
            hw.Update();
        }
    }

    private static List<IHardware> AllHardware(Computer computer)
    {
        List<IHardware> result = new();
        foreach (IHardware hw in computer.Hardware)
        {
            result.Add(hw);
        }

        return result;
    }

    private static List<IHardware> NvidiaHardware(Computer computer)
    {
        return AllHardware(computer).Where(h => h.GetType().Name == "NvidiaGpu").ToList();
    }

    private static float? ReadPower(IHardware hw)
    {
        ISensor sensor = hw.Sensors.FirstOrDefault(s => s.Name == "GPU Package");
        return sensor?.Value;
    }

    /// <summary>把传感器当前值清空（ISensor.Value 只读，只能反射其内部字段）。</summary>
    private static void ClearSensorValue(ISensor sensor)
    {
        if (sensor == null)
        {
            return;
        }

        FieldInfo current = sensor.GetType().GetField("_currentValue", BindingFlags.Instance | BindingFlags.NonPublic);
        current?.SetValue(sensor, null);
    }

    private static string Format(float? value) => value.HasValue ? value.Value.ToString("F1") : "(null)";

    private static string ResolveBusId(IHardware hw, Type nvml)
    {
        // 修复版：NvidiaGpu 直接保存 PCI 身份字符串。
        FieldInfo busIdField = hw.GetType().GetField("_nvmlPciBusId", BindingFlags.Instance | BindingFlags.NonPublic);
        if (busIdField != null)
        {
            return busIdField.GetValue(hw) as string;
        }

        // 0.9.6：只有缓存句柄，PCI 身份要从句柄反查。
        FieldInfo deviceField = hw.GetType().GetField("_nvmlDevice", BindingFlags.Instance | BindingFlags.NonPublic);
        object handle = deviceField?.GetValue(hw);
        if (handle == null || nvml == null)
        {
            return null;
        }

        MethodInfo pciInfo = nvml.GetMethod("NvmlDeviceGetPciInfo", AnyStatic);
        object info = pciInfo?.Invoke(null, new[] { handle });
        if (info == null)
        {
            return null;
        }

        return info.GetType().GetField("busId")?.GetValue(info) as string;
    }

    private static string HandleValue(object handle)
    {
        FieldInfo valueField = handle.GetType().GetField("Handle");
        object value = valueField?.GetValue(handle);
        return value?.ToString() ?? "?";
    }

    private static Assembly FindDiskInfoToolkitAssembly()
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(asm.GetName().Name, "DiskInfoToolkit", StringComparison.OrdinalIgnoreCase))
            {
                return asm;
            }
        }

        // 还没被加载时，从 LHM 的依赖里按名字装载。
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (AssemblyName name in asm.GetReferencedAssemblies())
                {
                    if (string.Equals(name.Name, "DiskInfoToolkit", StringComparison.OrdinalIgnoreCase))
                    {
                        return Assembly.Load(name);
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }

        return null;
    }

    private static string ReadInformationalVersion(Assembly asm)
    {
        try
        {
            System.Reflection.AssemblyInformationalVersionAttribute attr =
                asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion ?? "(none)";
        }
        catch
        {
            return "(error)";
        }
    }

    private static void Emit(string key, object value)
    {
        Console.WriteLine("PROBE|" + key + "=" + value);
    }
}
