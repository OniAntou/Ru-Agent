using System.Text;

namespace RuAgent.Broker.Tools.Terminal;

internal static class SpoolPageReader
{
    internal static string Read(string window, string? filePath, int start, int length)
    {
        var builder = new StringBuilder(length);
        if (start < window.Length)
        {
            builder.Append(window, start, Math.Min(length, window.Length - start));
        }

        var remaining = length - builder.Length;
        if (remaining > 0 && filePath is not null)
        {
            using var reader = new StreamReader(filePath, Encoding.UTF8);
            Skip(reader, start + builder.Length - window.Length);
            var buffer = new char[Math.Min(remaining, 4096)];
            while (remaining > 0)
            {
                var read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
                remaining -= read;
            }
        }

        return builder.ToString();
    }

    private static void Skip(StreamReader reader, int count)
    {
        var buffer = new char[4096];
        while (count > 0)
        {
            var read = reader.Read(buffer, 0, Math.Min(buffer.Length, count));
            if (read <= 0)
            {
                break;
            }

            count -= read;
        }
    }
}
