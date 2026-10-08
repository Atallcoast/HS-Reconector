using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using HearthstoneReconnector;

class AssetMaker
{
    static void Main(string[] args)
    {
        string output = args[0]; Directory.CreateDirectory(output);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var frames = new List<byte[]>();
        foreach(int size in sizes) using(var art = RuneArt.Render(size, .35f, false, false)) using(var stream = new MemoryStream()) { art.Save(stream, ImageFormat.Png); frames.Add(stream.ToArray()); }
        using(var stream = File.Create(Path.Combine(output, "reconnector.ico"))) using(var writer = new BinaryWriter(stream)) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for(int i = 0; i < sizes.Length; i++) {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach(var frame in frames) writer.Write(frame);
        }
        using(var art = RuneArt.Render(256, .35f, false, false)) art.Save(Path.Combine(output, "reconnector.png"), ImageFormat.Png);
        using(var preview = new Bitmap(420, 116)) using(var graphics = Graphics.FromImage(preview)) {
            graphics.Clear(Color.FromArgb(27, 23, 23));
            for(int i = 0; i < 4; i++) using(var art = RuneArt.Render(92, i == 1 || i == 2 ? 1 : 0, i == 2, i == 3)) graphics.DrawImageUnscaled(art, 12 + 102 * i, 12);
            preview.Save(Path.Combine(output, "button-preview.png"), ImageFormat.Png);
        }
        Console.WriteLine("Generated multi-resolution ICO and button artwork.");
    }
}
