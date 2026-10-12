namespace HazukiPdf.Imaging;

/// <summary>A decoded image: interleaved 8-bit samples, one or three per pixel.</summary>
internal sealed record RasterImage(byte[] Pixels, int Width, int Height, int Components);

/// <summary>
/// Decoder for sequential (baseline and extended) Huffman-coded JPEG images with one or three components.
/// Progressive, arithmetic-coded, lossless and 12-bit JPEGs throw <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class JpegDecoder
{
    private const int MaxPixels = 200_000_000;

    private readonly byte[] _data;
    private readonly int[][] _quantization = new int[4][];
    private readonly HuffmanTable?[] _dcTables = new HuffmanTable?[4];
    private readonly HuffmanTable?[] _acTables = new HuffmanTable?[4];
    private Component[] _components = Array.Empty<Component>();
    private int _width;
    private int _height;
    private int _maxH = 1;
    private int _maxV = 1;
    private int _mcusX;
    private int _mcusY;
    private int _restartInterval;
    private int _adobeTransform = -1;
    private bool _frameSeen;

    private JpegDecoder(byte[] data) => _data = data;

    /// <param name="data">The JPEG file.</param>
    /// <param name="colorTransform">The PDF /ColorTransform value, if given; it overrides the file's own marker.</param>
    public static RasterImage Decode(byte[] data, int? colorTransform = null) => new JpegDecoder(data).Run(colorTransform);

    private sealed class Component
    {
        public int Id;
        public int H;
        public int V;
        public int QuantizationId;
        public int DcTable;
        public int AcTable;
        public int BlocksPerLine;
        public int BlocksPerColumn;
        public byte[] Plane = Array.Empty<byte>();
        public int PlaneWidth;
        public int DcPrediction;
    }

    private sealed class HuffmanTable
    {
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValueOffset = new int[17];
        public readonly byte[] Values;

        // Fast lookup for codes of up to 9 bits: (length << 8) | value, or 0 if longer.
        public readonly ushort[] Lookup = new ushort[512];

        public HuffmanTable(byte[] counts, byte[] values)
        {
            Values = values;
            int code = 0;
            int k = 0;
            for (int length = 1; length <= 16; length++)
            {
                ValueOffset[length] = k - code;
                for (int i = 0; i < counts[length - 1]; i++)
                {
                    if (length <= 9)
                    {
                        int shift = 9 - length;
                        for (int fill = 0; fill < 1 << shift; fill++)
                        {
                            Lookup[(code << shift) | fill] = (ushort)((length << 8) | values[k]);
                        }
                    }

                    code++;
                    k++;
                }

                MaxCode[length] = counts[length - 1] > 0 ? code - 1 : -1;
                code <<= 1;
            }

            MaxCode[17] = int.MaxValue;
        }
    }

    private RasterImage Run(int? colorTransform)
    {
        int pos = 0;
        if (_data.Length < 4 || _data[0] != 0xFF || _data[1] != 0xD8)
        {
            throw new InvalidDataException("Not a JPEG file.");
        }

        pos = 2;
        while (pos < _data.Length)
        {
            if (_data[pos] != 0xFF || pos + 1 >= _data.Length)
            {
                pos++;
                continue;
            }

            byte marker = _data[pos + 1];
            pos += 2;
            if (marker == 0xFF || marker == 0x00 || marker is >= 0xD0 and <= 0xD7)
            {
                pos--;
                continue;
            }

            if (marker == 0xD9)
            {
                break;
            }

            if (pos + 2 > _data.Length)
            {
                break;
            }

            int length = (_data[pos] << 8) | _data[pos + 1];
            int segment = pos + 2;
            int segmentEnd = pos + length;
            if (segmentEnd > _data.Length || length < 2)
            {
                throw new InvalidDataException("Truncated JPEG segment.");
            }

            switch (marker)
            {
                case 0xDB:
                    ReadQuantization(segment, segmentEnd);
                    break;
                case 0xC4:
                    ReadHuffman(segment, segmentEnd);
                    break;
                case 0xC0:
                case 0xC1:
                    ReadFrame(segment);
                    break;
                case 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw new NotSupportedException("Only sequential Huffman JPEGs are supported.");
                case 0xDD:
                    _restartInterval = (_data[segment] << 8) | _data[segment + 1];
                    break;
                case 0xEE:
                    if (length >= 12 && _data[segment] == 'A' && _data[segment + 1] == 'd' && _data[segment + 2] == 'o' &&
                        _data[segment + 3] == 'b' && _data[segment + 4] == 'e')
                    {
                        _adobeTransform = _data[segment + 11];
                    }

                    break;
                case 0xDA:
                    segmentEnd = ReadScan(segment);
                    break;
            }

            pos = segmentEnd;
        }

        if (!_frameSeen)
        {
            throw new InvalidDataException("The JPEG has no frame.");
        }

        return BuildImage(colorTransform);
    }

    private void ReadQuantization(int pos, int end)
    {
        while (pos < end)
        {
            int precision = _data[pos] >> 4;
            int id = _data[pos] & 3;
            pos++;
            var table = new int[64];
            for (int i = 0; i < 64; i++)
            {
                int value = precision == 0 ? _data[pos++] : (_data[pos++] << 8) | _data[pos++];
                table[JpegCommon.ZigZag[i]] = value;
            }

            _quantization[id] = table;
        }
    }

    private void ReadHuffman(int pos, int end)
    {
        while (pos < end)
        {
            int tableClass = _data[pos] >> 4;
            int id = _data[pos] & 3;
            pos++;
            var counts = _data.AsSpan(pos, 16).ToArray();
            pos += 16;
            int total = counts.Sum(c => c);
            if (total > 256 || pos + total > end)
            {
                throw new InvalidDataException("Invalid Huffman table.");
            }

            var values = _data.AsSpan(pos, total).ToArray();
            pos += total;
            var table = new HuffmanTable(counts, values);
            if (tableClass == 0)
            {
                _dcTables[id] = table;
            }
            else
            {
                _acTables[id] = table;
            }
        }
    }

    private void ReadFrame(int pos)
    {
        if (_data[pos] != 8)
        {
            throw new NotSupportedException("Only 8-bit JPEGs are supported.");
        }

        _height = (_data[pos + 1] << 8) | _data[pos + 2];
        _width = (_data[pos + 3] << 8) | _data[pos + 4];
        int count = _data[pos + 5];
        if (_width == 0 || _height == 0 || (long)_width * _height > MaxPixels)
        {
            throw new NotSupportedException("Unsupported JPEG dimensions.");
        }

        if (count is not (1 or 3))
        {
            throw new NotSupportedException("Only grayscale and three-component JPEGs are supported.");
        }

        _components = new Component[count];
        for (int i = 0; i < count; i++)
        {
            int p = pos + 6 + (i * 3);
            var c = new Component
            {
                Id = _data[p],
                H = Math.Max(1, _data[p + 1] >> 4),
                V = Math.Max(1, _data[p + 1] & 15),
                QuantizationId = _data[p + 2] & 3,
            };
            _components[i] = c;
        }

        _maxH = _components.Max(c => c.H);
        _maxV = _components.Max(c => c.V);
        _mcusX = (_width + (8 * _maxH) - 1) / (8 * _maxH);
        _mcusY = (_height + (8 * _maxV) - 1) / (8 * _maxV);
        foreach (var c in _components)
        {
            c.BlocksPerLine = _mcusX * c.H;
            c.BlocksPerColumn = _mcusY * c.V;
            c.PlaneWidth = c.BlocksPerLine * 8;
            c.Plane = new byte[c.PlaneWidth * c.BlocksPerColumn * 8];
        }

        _frameSeen = true;
    }

    /// <summary>Decodes one scan and returns the position just after its entropy-coded data.</summary>
    private int ReadScan(int pos)
    {
        if (!_frameSeen)
        {
            throw new InvalidDataException("Scan before frame.");
        }

        int count = _data[pos];
        var scanComponents = new Component[count];
        for (int i = 0; i < count; i++)
        {
            int id = _data[pos + 1 + (i * 2)];
            int tables = _data[pos + 2 + (i * 2)];
            var c = _components.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("Unknown scan component.");
            c.DcTable = tables >> 4;
            c.AcTable = tables & 15;
            c.DcPrediction = 0;
            scanComponents[i] = c;
        }

        int dataStart = pos + 1 + (count * 2) + 3;
        var reader = new BitReader(_data, dataStart);
        Span<int> block = stackalloc int[64];

        if (count == 1)
        {
            // Non-interleaved: blocks cover only the component's own size.
            var c = scanComponents[0];
            int compWidth = ((_width * c.H) + _maxH - 1) / _maxH;
            int compHeight = ((_height * c.V) + _maxV - 1) / _maxV;
            int blocksX = (compWidth + 7) / 8;
            int blocksY = (compHeight + 7) / 8;
            int total = blocksX * blocksY;
            for (int n = 0; n < total; n++)
            {
                HandleRestart(reader, n, scanComponents);
                DecodeBlock(reader, c, block);
                StoreBlock(c, n % blocksX, n / blocksX, block);
            }
        }
        else
        {
            int total = _mcusX * _mcusY;
            for (int n = 0; n < total; n++)
            {
                HandleRestart(reader, n, scanComponents);
                int mx = n % _mcusX;
                int my = n / _mcusX;
                foreach (var c in scanComponents)
                {
                    for (int v = 0; v < c.V; v++)
                    {
                        for (int h = 0; h < c.H; h++)
                        {
                            DecodeBlock(reader, c, block);
                            StoreBlock(c, (mx * c.H) + h, (my * c.V) + v, block);
                        }
                    }
                }
            }
        }

        return reader.FindNextMarker();
    }

    private void HandleRestart(BitReader reader, int unit, Component[] scanComponents)
    {
        if (_restartInterval > 0 && unit > 0 && unit % _restartInterval == 0)
        {
            reader.Restart();
            foreach (var c in scanComponents)
            {
                c.DcPrediction = 0;
            }
        }
    }

    private void DecodeBlock(BitReader reader, Component c, Span<int> block)
    {
        block.Clear();
        var dc = _dcTables[c.DcTable] ?? throw new InvalidDataException("Missing DC table.");
        var ac = _acTables[c.AcTable] ?? throw new InvalidDataException("Missing AC table.");
        var q = _quantization[c.QuantizationId] ?? throw new InvalidDataException("Missing quantization table.");

        int t = DecodeHuffman(reader, dc);
        int diff = t == 0 ? 0 : Extend(reader.ReadBits(t), t);
        c.DcPrediction += diff;
        block[0] = c.DcPrediction * q[0];

        for (int k = 1; k < 64;)
        {
            int rs = DecodeHuffman(reader, ac);
            int s = rs & 15;
            int r = rs >> 4;
            if (s == 0)
            {
                if (r != 15)
                {
                    break;
                }

                k += 16;
                continue;
            }

            k += r;
            if (k > 63)
            {
                break;
            }

            int natural = JpegCommon.ZigZag[k];
            block[natural] = Extend(reader.ReadBits(s), s) * q[natural];
            k++;
        }
    }

    private static int DecodeHuffman(BitReader reader, HuffmanTable table)
    {
        int peek = reader.PeekBits(9);
        int entry = table.Lookup[peek];
        if (entry != 0)
        {
            reader.Skip(entry >> 8);
            return entry & 0xFF;
        }

        int code = 0;
        for (int length = 1; length <= 16; length++)
        {
            code = (code << 1) | reader.ReadBits(1);
            if (code <= table.MaxCode[length])
            {
                int index = table.ValueOffset[length] + code;
                return index >= 0 && index < table.Values.Length ? table.Values[index] : 0;
            }
        }

        return 0; // corrupt data; keep going like other decoders do
    }

    private static int Extend(int value, int bits) => value < 1 << (bits - 1) ? value - (1 << bits) + 1 : value;

    private static void StoreBlock(Component c, int blockX, int blockY, Span<int> coefficients)
    {
        if (blockX >= c.BlocksPerLine || blockY >= c.BlocksPerColumn)
        {
            return;
        }

        Span<float> block = stackalloc float[64];
        for (int i = 0; i < 64; i++)
        {
            block[i] = coefficients[i];
        }

        JpegCommon.InverseDct(block);
        int origin = (blockY * 8 * c.PlaneWidth) + (blockX * 8);
        for (int y = 0; y < 8; y++)
        {
            int row = origin + (y * c.PlaneWidth);
            for (int x = 0; x < 8; x++)
            {
                c.Plane[row + x] = (byte)Math.Clamp((int)MathF.Round(block[(y * 8) + x] + 128), 0, 255);
            }
        }
    }

    private RasterImage BuildImage(int? colorTransform)
    {
        int count = _components.Length;
        var pixels = new byte[_width * _height * count];
        bool transform = count == 3 && (colorTransform ?? (_adobeTransform >= 0 ? _adobeTransform : 1)) != 0;
        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                int o = ((y * _width) + x) * count;
                for (int i = 0; i < count; i++)
                {
                    var c = _components[i];
                    int sx = x * c.H / _maxH;
                    int sy = y * c.V / _maxV;
                    pixels[o + i] = c.Plane[(sy * c.PlaneWidth) + sx];
                }

                if (transform)
                {
                    float luma = pixels[o];
                    float cb = pixels[o + 1] - 128f;
                    float cr = pixels[o + 2] - 128f;
                    pixels[o] = ClampToByte(luma + (1.402f * cr));
                    pixels[o + 1] = ClampToByte(luma - (0.344136f * cb) - (0.714136f * cr));
                    pixels[o + 2] = ClampToByte(luma + (1.772f * cb));
                }
            }
        }

        return new RasterImage(pixels, _width, _height, count);
    }

    private static byte ClampToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _position;
        private uint _buffer;
        private int _count;
        private bool _hitMarker;

        public BitReader(byte[] data, int position)
        {
            _data = data;
            _position = position;
        }

        private void Fill(int needed)
        {
            while (_count < needed)
            {
                int b = 0;
                if (!_hitMarker && _position < _data.Length)
                {
                    b = _data[_position];
                    if (b == 0xFF)
                    {
                        byte next = _position + 1 < _data.Length ? _data[_position + 1] : (byte)0xD9;
                        if (next == 0)
                        {
                            _position += 2;
                        }
                        else
                        {
                            _hitMarker = true;
                            b = 0;
                        }
                    }
                    else
                    {
                        _position++;
                    }
                }

                _buffer = (_buffer << 8) | (uint)b;
                _count += 8;
            }
        }

        public int PeekBits(int n)
        {
            Fill(n);
            return (int)((_buffer >> (_count - n)) & ((1u << n) - 1));
        }

        public void Skip(int n) => _count -= n;

        public int ReadBits(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            int value = PeekBits(n);
            _count -= n;
            return value;
        }

        public void Restart()
        {
            _buffer = 0;
            _count = 0;
            _hitMarker = false;

            // Skip to and over the RSTn marker.
            while (_position + 1 < _data.Length)
            {
                if (_data[_position] == 0xFF && _data[_position + 1] is >= 0xD0 and <= 0xD7)
                {
                    _position += 2;
                    return;
                }

                _position++;
            }
        }

        /// <summary>Returns the position of the next marker that is not a restart marker.</summary>
        public int FindNextMarker()
        {
            int p = _position;
            while (p + 1 < _data.Length)
            {
                if (_data[p] == 0xFF && _data[p + 1] != 0 && _data[p + 1] is not (>= 0xD0 and <= 0xD7) && _data[p + 1] != 0xFF)
                {
                    return p;
                }

                p++;
            }

            return _data.Length;
        }
    }
}
