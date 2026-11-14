namespace OpenLLMCluster.Infrastructure;

/// <summary>Отдельное хранилище секретов: DPAPI текущего пользователя Windows, AES-GCM с ключом 0600 на Unix</summary>
public sealed class ProtectedSecretStore(string path)
{
    public string FilePath { get; } = path;

    /// <summary>Читает и расшифровывает секреты; повреждение не превращается в пустой набор</summary>
    public Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var data = File.ReadAllBytes(FilePath);
            var plaintext = OperatingSystem.IsWindows() ? Dpapi(data, protect: false) : DecryptUnix(data);
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) ?? throw new InvalidDataException("Пустое хранилище ключей"); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) { Trace.TraceError("Чтение защищённых ключей: {0}", ex.GetType().Name); throw; }
    }

    /// <summary>Записывает только зашифрованный набор, заменяя прежние значения и удалённые узлы</summary>
    public void Save(Dictionary<string, string> values)
    {
        byte[]? plain = null;
        var temporary = FilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            plain = JsonSerializer.SerializeToUtf8Bytes(values);
            var encrypted = OperatingSystem.IsWindows() ? Dpapi(plain, protect: true) : EncryptUnix(plain);
            WritePrivate(temporary, encrypted);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) { Trace.TraceError("Запись защищённых ключей: {0}", ex.GetType().Name); throw; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Шифрует значения аутентифицированным AES-GCM; ключ ограничен текущим Unix-пользователем
    private byte[] EncryptUnix(byte[] plain)
    {
        var keyPath = FilePath + ".key";
        if (!File.Exists(keyPath)) WritePrivate(keyPath, RandomNumberGenerator.GetBytes(32));
        var key = File.ReadAllBytes(keyPath);
        try
        {
            var result = new byte[28 + plain.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // Проверяет подлинность ciphertext; неизвестный пользователь или повреждение вызывают ошибку
    private byte[] DecryptUnix(byte[] data)
    {
        if (data.Length < 28) throw new InvalidDataException("Повреждено хранилище ключей");
        var key = File.ReadAllBytes(FilePath + ".key");
        try
        {
            var result = new byte[data.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), result);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // Создаёт Unix-файл сразу с приватными правами, без промежутка публичного доступа
    private static void WritePrivate(string file, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var output = new FileStream(file, options);
        output.Write(bytes);
    }

    // Вызывает штатную DPAPI без добавления пакета и без машинного режима шифрования
    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var success = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
