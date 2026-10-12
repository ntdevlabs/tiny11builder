namespace Tiny11Builder.Core;

public static class IsoStructure
{
    public static async Task ValidateAsync(Stream stream, CancellationToken token)
    {
        if (stream.Length < 34816 || stream.Length % 2048 != 0)
            throw new InvalidDataException("The file is not a valid ISO-sized image.");
        var descriptor = new byte[2048 * 16];
        stream.Position = 2048 * 16;
        var count = 0;
        while (count < descriptor.Length)
        {
            var read = await stream.ReadAsync(descriptor.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        for (var offset = 1; offset + 5 <= count; offset += 2048)
            if (System.Text.Encoding.ASCII.GetString(descriptor, offset, 5) is "CD001" or "BEA01" or "NSR02" or "NSR03")
            {
                stream.Position = 0;
                return;
            }
        throw new InvalidDataException("The file has no recognized ISO/UDF volume descriptor.");
    }
}
