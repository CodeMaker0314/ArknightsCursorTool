using Microsoft.Win32;
using System.Text;

namespace ArknightsCursorTool;

internal sealed class AniCursorAnimation
{
    private const int MaximumSteps = 4096;
    private readonly int[] _frameIndices;
    private readonly long[] _stepEndTicks;
    private readonly long _cycleTicks;

    public int StepCount => _frameIndices.Length;

    private AniCursorAnimation(int[] frameIndices, long[] stepEndTicks, long cycleTicks)
    {
        _frameIndices = frameIndices;
        _stepEndTicks = stepEndTicks;
        _cycleTicks = cycleTicks;
    }

    public int GetFrameIndex(TimeSpan elapsed)
    {
        if (_frameIndices.Length == 1 || _cycleTicks <= 0) return _frameIndices[0];

        var position = elapsed.Ticks % _cycleTicks;
        if (position < 0) position += _cycleTicks;
        var step = Array.BinarySearch(_stepEndTicks, position + 1);
        if (step < 0) step = ~step;
        return _frameIndices[Math.Min(step, _frameIndices.Length - 1)];
    }

    public static AniCursorAnimation? TryLoadSystemArrow()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", writable: false);
            var value = key?.GetValue("Arrow") as string;
            if (string.IsNullOrWhiteSpace(value)) return null;

            var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!string.Equals(Path.GetExtension(path), ".ani", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(path)) return null;

            return TryLoad(path);
        }
        catch
        {
            // Cursor mapping should remain available if the active scheme cannot be read.
            return null;
        }
    }

    private static AniCursorAnimation? TryLoad(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 12 || ReadFourCc(reader) != "RIFF") return null;

        var riffSize = reader.ReadUInt32();
        if (ReadFourCc(reader) != "ACON") return null;
        var riffEnd = Math.Min(stream.Length, 8L + riffSize);

        uint frameCount = 0;
        uint stepCount = 0;
        uint defaultRate = 6;
        uint[]? rates = null;
        uint[]? sequence = null;

        while (stream.Position + 8 <= riffEnd)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadUInt32();
            var chunkStart = stream.Position;
            var chunkEnd = chunkStart + chunkSize;
            if (chunkEnd > riffEnd || chunkEnd > stream.Length) return null;

            if (chunkId == "anih" && chunkSize >= 36)
            {
                _ = reader.ReadUInt32(); // cbSizeOf
                frameCount = reader.ReadUInt32();
                stepCount = reader.ReadUInt32();
                _ = reader.ReadUInt32(); // cx
                _ = reader.ReadUInt32(); // cy
                _ = reader.ReadUInt32(); // cBitCount
                _ = reader.ReadUInt32(); // cPlanes
                defaultRate = reader.ReadUInt32();
                _ = reader.ReadUInt32(); // flags
            }
            else if ((chunkId == "rate" || chunkId == "seq ") && chunkSize % 4 == 0 && chunkSize / 4 <= MaximumSteps)
            {
                var count = (int)(chunkSize / 4);
                var values = new uint[count];
                for (var i = 0; i < count; i++) values[i] = reader.ReadUInt32();
                if (chunkId == "rate") rates = values;
                else sequence = values;
            }

            stream.Position = chunkEnd + (chunkSize & 1);
        }

        if (frameCount == 0 || stepCount == 0 || frameCount > MaximumSteps || stepCount > MaximumSteps) return null;

        var frameIndices = new int[stepCount];
        var stepEndTicks = new long[stepCount];
        var cycleTicks = 0L;
        var fallbackRate = defaultRate == 0 ? 6u : defaultRate;

        for (var step = 0; step < stepCount; step++)
        {
            var frame = sequence is not null && step < sequence.Length ? sequence[step] : (uint)step;
            frameIndices[step] = (int)(frame % frameCount);

            var rate = rates is not null && step < rates.Length && rates[step] > 0 ? rates[step] : fallbackRate;
            var durationTicks = Math.Max(1L, (long)Math.Round(rate * (double)TimeSpan.TicksPerSecond / 60d));
            cycleTicks = checked(cycleTicks + durationTicks);
            stepEndTicks[step] = cycleTicks;
        }

        return cycleTicks == 0 ? null : new AniCursorAnimation(frameIndices, stepEndTicks, cycleTicks);
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return bytes.Length == 4 ? Encoding.ASCII.GetString(bytes) : string.Empty;
    }
}
