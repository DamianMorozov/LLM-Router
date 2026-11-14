namespace OpenLLMCluster.Infrastructure;

/// <summary>Читает расположение и точные размеры тензоров библиотекой той же сборки, что выполняет RPC</summary>
public static class NativeGgufLayout
{
    [StructLayout(LayoutKind.Sequential)]
    private struct InitParameters { public byte NoAllocation; public IntPtr Context; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr InitFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, InitParameters parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeContext(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long TensorCount(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint DataOffset(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint TensorValue(IntPtr context, long index);

    /// <summary>Не выделяет память под веса и освобождает нативный контекст после чтения метаданных</summary>
    public static IReadOnlyList<GgufTensorRange> Read(string path, string runtimeDirectory)
    {
        var libraryName = OperatingSystem.IsWindows() ? "ggml-base.dll" : OperatingSystem.IsMacOS() ? "libggml-base.dylib" : "libggml-base.so";
        var library = NativeLibrary.Load(Path.GetFullPath(Path.Combine(runtimeDirectory, libraryName)));
        try
        {
            var init = Export<InitFile>(library, "gguf_init_from_file");
            var free = Export<FreeContext>(library, "gguf_free");
            var count = Export<TensorCount>(library, "gguf_get_n_tensors");
            var start = Export<DataOffset>(library, "gguf_get_data_offset");
            var offset = Export<TensorValue>(library, "gguf_get_tensor_offset");
            var size = Export<TensorValue>(library, "gguf_get_tensor_size");
            var context = init(path, new() { NoAllocation = 1 });
            if (context == IntPtr.Zero) throw new InvalidDataException("Библиотека llama.cpp не смогла прочитать GGUF");
            try
            {
                var total = count(context);
                if (total is < 1 or > 10000000) throw new InvalidDataException("Недопустимое количество тензоров GGUF");
                var dataOffset = checked((long)start(context));
                var ranges = new List<GgufTensorRange>();
                for (long index = 0; index < total; index++)
                    ranges.Add(new(checked(dataOffset + (long)offset(context, index)), checked((long)size(context, index))));
                return ranges;
            }
            finally { free(context); }
        }
        catch (Exception ex) { Trace.TraceError("Разметка GGUF: {0}", ex.GetType().Name); throw; }
        finally { NativeLibrary.Free(library); }
    }

    // Получает только фиксированные экспорты из выбранной локальной сборки
    private static T Export<T>(IntPtr library, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
}
