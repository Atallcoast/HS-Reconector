using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace HearthstoneReconnector {
    internal static class LayeredSurface {
        [StructLayout(LayoutKind.Sequential)] struct Point { public int X,Y; public Point(int x,int y){X=x;Y=y;} }
        [StructLayout(LayoutKind.Sequential)] struct Size { public int Width,Height; public Size(int w,int h){Width=w;Height=h;} }
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend { public byte Operation,Flags,Alpha,Format; }
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window,IntPtr dest,ref Point position,ref Size size,IntPtr source,ref Point origin,int key,ref Blend blend,int flags);
        internal static void Present(IntPtr window,Bitmap bitmap,System.Drawing.Point location) {
            IntPtr screen=GetDC(IntPtr.Zero),dc=IntPtr.Zero,hbitmap=IntPtr.Zero,old=IntPtr.Zero;
            try {
                dc=CreateCompatibleDC(screen); hbitmap=bitmap.GetHbitmap(Color.FromArgb(0));old=SelectObject(dc,hbitmap);
                var position=new Point(location.X,location.Y);var size=new Size(bitmap.Width,bitmap.Height);var origin=new Point(0,0);
                var blend=new Blend{Alpha=255,Format=1};
                if(!UpdateLayeredWindow(window,screen,ref position,ref size,dc,ref origin,0,ref blend,2))throw new Win32Exception(Marshal.GetLastWin32Error());
            } finally { if(old!=IntPtr.Zero)SelectObject(dc,old);if(hbitmap!=IntPtr.Zero)DeleteObject(hbitmap);if(dc!=IntPtr.Zero)DeleteDC(dc);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen); }
        }
    }
}
