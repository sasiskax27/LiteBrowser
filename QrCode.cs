using System.Text;

namespace LiteBrowser;

/// <summary>
/// Генератор QR-кодов (байтовый режим, уровень коррекции M, версии 1–10 — до ~210 байт).
/// По спецификации ISO/IEC 18004, без сторонних библиотек.
/// </summary>
static class QrCode
{
    static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    static readonly int[] NumBlocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

    static int RawModules(int ver)
    {
        int r = (16 * ver + 128) * ver + 64;
        if (ver >= 2) { int na = ver / 7 + 2; r -= (25 * na - 10) * na - 55; if (ver >= 7) r -= 36; }
        return r;
    }

    static int DataCodewords(int ver) => RawModules(ver) / 8 - EccPerBlock[ver] * NumBlocks[ver];

    public static bool[,] Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        int ver = 1;
        for (; ver <= 10; ver++)
        {
            int ccBits = ver < 10 ? 8 : 16;
            if (4 + ccBits + bytes.Length * 8 <= DataCodewords(ver) * 8) break;
        }
        if (ver > 10) throw new ArgumentException("Слишком длинный адрес для QR-кода");

        // Биты данных
        var bits = new List<bool>();
        void Append(int val, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }
        Append(4, 4);
        Append(bytes.Length, ver < 10 ? 8 : 16);
        foreach (var b in bytes) Append(b, 8);
        int capBits = DataCodewords(ver) * 8;
        Append(0, Math.Min(4, capBits - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capBits; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var data = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        var codewords = AddEccAndInterleave(data, ver);
        int size = ver * 4 + 17;
        var modules = new bool[size, size];
        var isFunc = new bool[size, size];
        void Set(int x, int y, bool dark) { modules[y, x] = dark; isFunc[y, x] = true; }

        // Синхронизирующие линии
        for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        // Поисковые узоры
        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(x, y, dist != 2 && dist != 4);
                }
        // Выравнивающие узоры
        var align = AlignmentPositions(ver);
        for (int i = 0; i < align.Length; i++)
            for (int j = 0; j < align.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == align.Length - 1) || (i == align.Length - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        DrawFormat(0, size, Set);   // резервируем место
        DrawVersion(ver, size, Set);

        // Размещение данных зигзагом
        int bit = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (isFunc[y, x] || bit >= codewords.Length * 8) continue;
                    modules[y, x] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                    bit++;
                }
        }

        // Выбор маски с наименьшим штрафом
        int bestMask = 0;
        long bestPenalty = long.MaxValue;
        for (int m = 0; m < 8; m++)
        {
            ApplyMask(modules, isFunc, m);
            DrawFormat(m, size, Set);
            long p = Penalty(modules);
            if (p < bestPenalty) { bestPenalty = p; bestMask = m; }
            ApplyMask(modules, isFunc, m); // снять
        }
        ApplyMask(modules, isFunc, bestMask);
        DrawFormat(bestMask, size, Set);
        return modules;
    }

    static int[] AlignmentPositions(int ver) => ver switch
    {
        1 => Array.Empty<int>(),
        2 => new[] { 6, 18 }, 3 => new[] { 6, 22 }, 4 => new[] { 6, 26 }, 5 => new[] { 6, 30 }, 6 => new[] { 6, 34 },
        7 => new[] { 6, 22, 38 }, 8 => new[] { 6, 24, 42 }, 9 => new[] { 6, 26, 46 }, _ => new[] { 6, 28, 50 },
    };

    static void DrawFormat(int mask, int size, Action<int, int, bool> set)
    {
        int data = (0 << 3) | mask; // уровень M = 00
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;
        bool B(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++) set(8, i, B(i));
        set(8, 7, B(6)); set(8, 8, B(7)); set(7, 8, B(8));
        for (int i = 9; i < 15; i++) set(14 - i, 8, B(i));
        for (int i = 0; i < 8; i++) set(size - 1 - i, 8, B(i));
        for (int i = 8; i < 15; i++) set(8, size - 15 + i, B(i));
        set(8, size - 8, true);
    }

    static void DrawVersion(int ver, int size, Action<int, int, bool> set)
    {
        if (ver < 7) return;
        int rem = ver;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        long bits = ((long)ver << 12) | (long)rem;
        for (int i = 0; i < 18; i++)
        {
            bool b = ((bits >> i) & 1) != 0;
            int a = size - 11 + i % 3, c = i / 3;
            set(a, c, b);
            set(c, a, b);
        }
    }

    static void ApplyMask(bool[,] m, bool[,] f, int mask)
    {
        int size = m.GetLength(0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (f[y, x]) continue;
                bool inv = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (inv) m[y, x] = !m[y, x];
            }
    }

    static long Penalty(bool[,] m)
    {
        int size = m.GetLength(0);
        long p = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? m[a, b] : m[b, a], prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                    if (cur == prev) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
        int dark = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (m[y, x]) dark++;
                if (x < size - 1 && y < size - 1 && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) p += 3;
            }
        int total = size * size;
        p += (Math.Abs(dark * 20 - total * 10) + total - 1) / total * 10;
        return p;
    }

    static byte[] AddEccAndInterleave(byte[] data, int ver)
    {
        int nb = NumBlocks[ver], ecLen = EccPerBlock[ver];
        int raw = RawModules(ver) / 8;
        int numShort = nb - raw % nb, shortLen = raw / nb;
        var div = RsDivisor(ecLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < nb; i++)
        {
            int datLen = shortLen - ecLen + (i < numShort ? 0 : 1);
            var dat = data.Skip(k).Take(datLen).ToArray();
            k += datLen;
            var ecc = RsRemainder(dat, div);
            var block = new byte[shortLen + 1];
            Array.Copy(dat, block, dat.Length);
            Array.Copy(ecc, 0, block, block.Length - ecLen, ecLen);
            blocks.Add(block);
        }
        var result = new List<byte>();
        for (int i = 0; i < shortLen + 1; i++)
            for (int j = 0; j < blocks.Count; j++)
                if (i != shortLen - ecLen || j >= numShort) result.Add(blocks[j][i]);
        return result.ToArray();
    }

    static byte Mul(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
        return (byte)z;
    }

    static byte[] RsDivisor(int degree)
    {
        var r = new byte[degree];
        r[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                r[j] = Mul(r[j], root);
                if (j + 1 < degree) r[j] ^= r[j + 1];
            }
            root = Mul(root, 2);
        }
        return r;
    }

    static byte[] RsRemainder(byte[] data, byte[] div)
    {
        var r = new byte[div.Length];
        foreach (var b in data)
        {
            int factor = b ^ r[0];
            Array.Copy(r, 1, r, 0, r.Length - 1);
            r[^1] = 0;
            for (int i = 0; i < r.Length; i++) r[i] ^= Mul(div[i], factor);
        }
        return r;
    }

    public static Bitmap Render(string text, int scale = 8)
    {
        var m = Encode(text);
        int size = m.GetLength(0), border = 4;
        var bmp = new Bitmap((size + border * 2) * scale, (size + border * 2) * scale);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (m[y, x]) g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
        return bmp;
    }
}

/// <summary>Окно с QR-кодом текущей страницы — навести камеру телефона и открыть там.</summary>
sealed class QrForm : Form
{
    public QrForm(string url)
    {
        Text = "QR-код страницы";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Menu;
        ForeColor = Theme.Text;
        var img = QrCode.Render(url, 7);
        var pic = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(Theme.S(20), Theme.S(20)) };
        var hint = new Label
        {
            Text = "Наведите камеру телефона, чтобы открыть страницу на нём",
            AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.TextDim,
            Font = Theme.GetFont("Segoe UI", 9f), Location = new Point(Theme.S(10), pic.Bottom + Theme.S(8) + img.Height - pic.Height),
        };
        Controls.Add(pic);
        ClientSize = new Size(img.Width + Theme.S(40), img.Height + Theme.S(80));
        hint.SetBounds(Theme.S(10), Theme.S(20) + img.Height + Theme.S(6), ClientSize.Width - Theme.S(20), Theme.S(40));
        Controls.Add(hint);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, Theme.Menu); } catch { }
    }
}
