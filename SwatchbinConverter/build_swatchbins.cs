using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public uint Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
    public uint EsramOffsetBytes;
    public uint EsramUsageBytes;
    public uint TileMode;
    public uint Pitch;
}

internal sealed class XgTextureComputer : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong GetSizeDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong ReleaseDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CopyDelegate(
        IntPtr self,
        IntPtr destination,
        uint destinationSubresource,
        uint destinationOffset,
        IntPtr source,
        uint sourceRowPitch,
        uint sourceDepthPitch);

    [DllImport("xg.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XGCreateTexture2DComputer(
        ref Texture2DDesc description,
        out IntPtr computer);

    private readonly IntPtr _computer;
    private readonly GetSizeDelegate _getSize;
    private readonly ReleaseDelegate _release;
    private readonly CopyDelegate _copyInto;
    private readonly CopyDelegate _copyFrom;

    public ulong ResourceSize { get { return _getSize(_computer); } }

    public XgTextureComputer(uint width, uint height, uint tileMode)
    {
        var description = new Texture2DDesc
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = 98,
            SampleCount = 1,
            TileMode = tileMode,
        };
        int result = XGCreateTexture2DComputer(ref description, out IntPtr computer);
        if (result != 0 || computer == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"XGCreateTexture2DComputer failed with HRESULT 0x{result:x8}.");
        }
        _computer = computer;

        IntPtr vtable = Marshal.ReadIntPtr(_computer);
        _release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
            Marshal.ReadIntPtr(vtable, 8));
        _getSize = Marshal.GetDelegateForFunctionPointer<GetSizeDelegate>(
            Marshal.ReadIntPtr(vtable, 24));
        _copyInto = Marshal.GetDelegateForFunctionPointer<CopyDelegate>(
            Marshal.ReadIntPtr(vtable, 64));
        _copyFrom = Marshal.GetDelegateForFunctionPointer<CopyDelegate>(
            Marshal.ReadIntPtr(vtable, 72));
    }

    public byte[] Detile(byte[] tiled, uint rowPitch, uint depthPitch)
    {
        byte[] linear = new byte[depthPitch];
        GCHandle linearPin = GCHandle.Alloc(linear, GCHandleType.Pinned);
        GCHandle tiledPin = GCHandle.Alloc(tiled, GCHandleType.Pinned);
        try
        {
            int result = _copyFrom(
                _computer, linearPin.AddrOfPinnedObject(), 0, 0,
                tiledPin.AddrOfPinnedObject(), rowPitch, depthPitch);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"XG CopyFromSubresource failed with HRESULT 0x{result:x8}.");
            }
            return linear;
        }
        finally
        {
            tiledPin.Free();
            linearPin.Free();
        }
    }

    public byte[] Tile(byte[] linear, uint rowPitch, uint depthPitch)
    {
        byte[] tiled = new byte[checked((int)ResourceSize)];
        GCHandle linearPin = GCHandle.Alloc(linear, GCHandleType.Pinned);
        GCHandle tiledPin = GCHandle.Alloc(tiled, GCHandleType.Pinned);
        try
        {
            int result = _copyInto(
                _computer, tiledPin.AddrOfPinnedObject(), 0, 0,
                linearPin.AddrOfPinnedObject(), rowPitch, depthPitch);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"XG CopyIntoSubresource failed with HRESULT 0x{result:x8}.");
            }
            return tiled;
        }
        finally
        {
            tiledPin.Free();
            linearPin.Free();
        }
    }

    public void Dispose()
    {
        _release(_computer);
    }
}

internal static class SwatchbinBuilder
{
    private const int BundleHeaderSize = 83;
    private static readonly byte[] PngSignature =
        { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] TileModes = { 13, 14 };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string path);

    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine(
                "Usage: build_swatchbins <ForzaTech directory> <stock swatchbin directory> <PNG source directory> <output directory>");
            return 2;
        }

        string forzaTechDirectory = Path.GetFullPath(args[0]);
        string stockDirectory = Path.GetFullPath(args[1]);
        string pngDirectory = Path.GetFullPath(args[2]);
        string outputDirectory = Path.GetFullPath(args[3]);
        string libraryDirectory = Path.Combine(forzaTechDirectory, "libs");
        if (!Directory.Exists(forzaTechDirectory) ||
            !File.Exists(Path.Combine(forzaTechDirectory, "xg.dll")) ||
            !File.Exists(Path.Combine(libraryDirectory, "BCnEncoder.dll")) ||
            !File.Exists(Path.Combine(libraryDirectory, "Microsoft.Toolkit.HighPerformance.dll")))
        {
            throw new DirectoryNotFoundException(
                "ForzaTech directory is missing xg.dll or required BCnEncoder libraries.");
        }
        if (!Directory.Exists(stockDirectory) || !Directory.Exists(pngDirectory))
        {
            throw new FileNotFoundException(
                "The stock swatchbin or source PNG directory could not be found.");
        }

        if (!SetDllDirectoryW(forzaTechDirectory))
        {
            throw new InvalidOperationException("Could not add ForzaTech to the native DLL search path.");
        }
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            string name = new AssemblyName(eventArgs.Name).Name + ".dll";
            string candidate = Path.Combine(libraryDirectory, name);
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
        Assembly.LoadFrom(Path.Combine(libraryDirectory, "BCnEncoder.dll"));
        Assembly.LoadFrom(Path.Combine(
            libraryDirectory, "Microsoft.Toolkit.HighPerformance.dll"));

        Directory.CreateDirectory(outputDirectory);
        string[] stockFiles = Directory.GetFiles(
            stockDirectory, "*.swatchbin", SearchOption.TopDirectoryOnly);
        if (stockFiles.Length == 0)
        {
            throw new InvalidDataException(
                $"No .swatchbin files were found in '{stockDirectory}'.");
        }

        foreach (string stockPath in stockFiles)
        {
            string filename = Path.GetFileName(stockPath);
            string pngPath = Path.Combine(
                pngDirectory, Path.GetFileNameWithoutExtension(filename) + ".png");
            byte[] stock = File.ReadAllBytes(stockPath);
            byte[] replacement = BuildReplacement(stock, pngPath);
            File.WriteAllBytes(Path.Combine(outputDirectory, filename), replacement);
            Console.WriteLine($"Created {filename}");
        }

        Console.WriteLine(
            $"Created {stockFiles.Length} native Durango/BC7 bundles in {outputDirectory}");
        return 0;
    }

    private static byte[] BuildReplacement(byte[] stock, string pngPath)
    {
        if (stock.Length <= BundleHeaderSize ||
            Encoding.ASCII.GetString(stock, 0, 4) != "burG" ||
            Encoding.ASCII.GetString(stock, 16, 4) != "BCXT" ||
            Encoding.ASCII.GetString(stock, 40, 4) != "HCXT")
        {
            throw new InvalidDataException("Stock file is not the expected TXCB swatchbin.");
        }

        uint width = BitConverter.ToUInt16(stock, 64);
        uint height = BitConverter.ToUInt16(stock, 66);
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("Stock texture has zero dimensions.");
        }
        int rowPitch = checked((int)((width + 3) / 4 * 16));
        int depthPitch = checked(rowPitch * (int)((height + 3) / 4));
        byte[] rgba = ReadPngRgba(pngPath, width, height);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            rgba[i] = (byte)((rgba[i] * alpha + 127) / 255);
            rgba[i + 1] = (byte)((rgba[i + 1] * alpha + 127) / 255);
            rgba[i + 2] = (byte)((rgba[i + 2] * alpha + 127) / 255);
        }

        var encoder = new BcEncoder(
            (CompressionFormat)Enum.Parse(typeof(CompressionFormat), "BC7", true));
        encoder.OutputOptions.GenerateMipMaps = false;
        byte[][] mipData = encoder.EncodeToRawBytes(
            rgba, (int)width, (int)height,
            (PixelFormat)Enum.Parse(typeof(PixelFormat), "Rgba32", true));
        if (mipData.Length != 1 || mipData[0].Length != depthPitch)
        {
            throw new InvalidDataException("BC7 encoder returned an unexpected mip layout.");
        }

        uint tileMode = FindStockTileMode(
            stock.AsSpan(BundleHeaderSize).ToArray(), width, height,
            (uint)rowPitch, (uint)depthPitch);
        using var computer = new XgTextureComputer(width, height, tileMode);
        byte[] tiled = computer.Tile(mipData[0], (uint)rowPitch, (uint)depthPitch);
        if (tiled.Length != stock.Length - BundleHeaderSize)
        {
            throw new InvalidDataException(
                "Generated texture payload size does not match the stock bundle.");
        }
        if (!computer.Detile(tiled, (uint)rowPitch, (uint)depthPitch)
                .SequenceEqual(mipData[0]))
        {
            throw new InvalidDataException("Generated tiled data failed its BC7 round-trip check.");
        }

        byte[] result = new byte[BundleHeaderSize + tiled.Length];
        Buffer.BlockCopy(stock, 0, result, 0, BundleHeaderSize);
        Buffer.BlockCopy(tiled, 0, result, BundleHeaderSize, tiled.Length);
        return result;
    }

    private static uint FindStockTileMode(
        byte[] stockPayload,
        uint width,
        uint height,
        uint rowPitch,
        uint depthPitch)
    {
        uint? match = null;
        foreach (uint tileMode in TileModes)
        {
            using var computer = new XgTextureComputer(width, height, tileMode);
            if (computer.ResourceSize != (ulong)stockPayload.Length)
            {
                continue;
            }

            byte[] linear = computer.Detile(stockPayload, rowPitch, depthPitch);
            if (!computer.Tile(linear, rowPitch, depthPitch).SequenceEqual(stockPayload))
            {
                continue;
            }
            if (match.HasValue)
            {
                throw new InvalidDataException(
                    "Stock swatchbin matches more than one XG tile mode.");
            }
            match = tileMode;
        }

        if (!match.HasValue)
        {
            throw new InvalidDataException(
                "Could not identify a stock XG tile mode by exact payload round-trip.");
        }
        return match.Value;
    }

    private static byte[] ReadPngRgba(string path, uint expectedWidth, uint expectedHeight)
    {
        byte[] png = File.ReadAllBytes(path);
        if (png.Length < PngSignature.Length ||
            !png.Take(PngSignature.Length).SequenceEqual(PngSignature))
        {
            throw new InvalidDataException($"{path} is not a PNG file.");
        }

        uint width = 0;
        uint height = 0;
        bool foundHeader = false;
        bool foundEnd = false;
        using var compressed = new MemoryStream();
        int offset = PngSignature.Length;
        while (offset <= png.Length - 12)
        {
            uint chunkLength = ReadBigEndianUInt32(png, offset);
            if (chunkLength > int.MaxValue ||
                (long)offset + 12 + chunkLength > png.Length)
            {
                throw new InvalidDataException($"{path} has an invalid PNG chunk length.");
            }

            int dataOffset = offset + 8;
            string chunkType = Encoding.ASCII.GetString(png, offset + 4, 4);
            int length = (int)chunkLength;
            if (chunkType == "IHDR")
            {
                if (foundHeader || length != 13)
                {
                    throw new InvalidDataException($"{path} has an invalid PNG header.");
                }
                width = ReadBigEndianUInt32(png, dataOffset);
                height = ReadBigEndianUInt32(png, dataOffset + 4);
                if (png[dataOffset + 8] != 8 || png[dataOffset + 9] != 6 ||
                    png[dataOffset + 10] != 0 || png[dataOffset + 11] != 0 ||
                    png[dataOffset + 12] != 0)
                {
                    throw new InvalidDataException(
                        $"{path} must be non-interlaced RGBA8 PNG.");
                }
                foundHeader = true;
            }
            else if (chunkType == "IDAT")
            {
                if (!foundHeader)
                {
                    throw new InvalidDataException($"{path} has image data before IHDR.");
                }
                compressed.Write(png, dataOffset, length);
            }
            else if (chunkType == "IEND")
            {
                foundEnd = true;
                break;
            }
            offset = checked(offset + 12 + length);
        }

        if (!foundHeader || !foundEnd || width != expectedWidth || height != expectedHeight)
        {
            throw new InvalidDataException(
                $"{path} dimensions are {width}x{height}; expected {expectedWidth}x{expectedHeight}.");
        }

        int stride = checked((int)width * 4);
        int scanlineSize = checked(stride + 1);
        int filteredSize = checked(scanlineSize * (int)height);
        compressed.Position = 0;
        byte[] filtered = new byte[filteredSize];
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, true))
        {
            ReadExactly(zlib, filtered);
            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException($"{path} expands beyond its declared dimensions.");
            }
        }

        byte[] rgba = new byte[checked(stride * (int)height)];
        for (int y = 0; y < height; y++)
        {
            int inputOffset = y * scanlineSize;
            int outputOffset = y * stride;
            byte filter = filtered[inputOffset++];
            if (filter > 4)
            {
                throw new InvalidDataException($"{path} uses an unknown PNG row filter.");
            }
            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? rgba[outputOffset + x - 4] : 0;
                int above = y > 0 ? rgba[outputOffset - stride + x] : 0;
                int upperLeft = y > 0 && x >= 4
                    ? rgba[outputOffset - stride + x - 4]
                    : 0;
                int predictor = filter == 1 ? left
                    : filter == 2 ? above
                    : filter == 3 ? (left + above) / 2
                    : filter == 4 ? Paeth(left, above, upperLeft)
                    : 0;
                rgba[outputOffset + x] =
                    unchecked((byte)(filtered[inputOffset + x] + predictor));
            }
        }
        return rgba;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int estimate = left + above - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int aboveDistance = Math.Abs(estimate - above);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) | data[offset + 3];
    }

    private static void ReadExactly(Stream stream, byte[] output)
    {
        int offset = 0;
        while (offset < output.Length)
        {
            int read = stream.Read(output, offset, output.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException("PNG image data ended unexpectedly.");
            }
            offset += read;
        }
    }
}
