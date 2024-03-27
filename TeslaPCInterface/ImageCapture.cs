static class Screen
{


    public static IEnumerable<Image> Snapshots()
    {
        return Screen.Snapshots(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height, true);
    }

    /// <summary>
    /// Returns a 
    /// </summary>
    /// <param name="delayTime"></param>
    /// <returns></returns>
    public static IEnumerable<Image> Snapshots(int width, int height, bool showCursor)
    {
        SetDpiAwareness();
        Size size = new(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);


        Bitmap srcImage = new(size.Width, size.Height);
        Graphics srcGraphics = Graphics.FromImage(srcImage);

        bool scaled = (width != size.Width || height != size.Height);

        var dstImage = srcImage;
        var dstGraphics = srcGraphics;

        if (scaled)
        {
            Console.WriteLine("Scaling images to " + width + "x" + height);
            //resize the image to the specified width and height
            dstImage = new Bitmap(width, height);
            dstGraphics = Graphics.FromImage(dstImage);
        }

        Rectangle src = new(0, 0, size.Width, size.Height);
        Rectangle dst = new(0, 0, width, height);
        Size curSize = new(32, 32);

        while (true)
        {
            srcGraphics.CopyFromScreen(0, 0, 0, 0, size);

            //if (showCursor)
            //  Cursors.Default.Draw(srcGraphics, new Rectangle(Cursor.Position, curSize));

            if (scaled)
                dstGraphics.DrawImage(srcImage, dst, src, GraphicsUnit.Pixel);

            yield return dstImage;

        }


    }
    private enum ProcessDPIAwareness
    {
        ProcessDPIUnaware = 0,
        ProcessSystemDPIAware = 1,
        ProcessPerMonitorDPIAware = 2
    }

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(ProcessDPIAwareness value);

    public static void SetDpiAwareness()
    {
        try
        {
            if (Environment.OSVersion.Version.Major >= 6)
            {
                SetProcessDpiAwareness(ProcessDPIAwareness.ProcessPerMonitorDPIAware);
            }
        }
        catch (EntryPointNotFoundException)//this exception occures if OS does not implement this API, just ignore it.
        {
        }
    }

    internal static IEnumerable<MemoryStream> Streams(this IEnumerable<Image> source)
    {
        var ms = new MemoryStream();

        foreach (var img in source)
        {
            ms.SetLength(0);
            img.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            yield return ms;
        }

        ms.Close();


    }


}