using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace ClipBar.Library;

internal static class ShellFileOps
{
    public static void RecycleFile(string path)
    {
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"Удаление отменено: {path}");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"Не удалось переместить в корзину: {ex.Message}", ex);
        }
    }
}
