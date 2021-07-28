using System;
using System.Runtime.InteropServices;

public static partial class AsyncImageLoader {
  public class FreeImage {
    public enum Filter {
      FILTER_BOX = 0,
      FILTER_BICUBIC = 1,
      FILTER_BILINEAR = 2,
      FILTER_BSPLINE = 3,
      FILTER_CATMULLROM = 4,
      FILTER_LANCZOS3 = 5
    }

    public enum Format {
      FIF_UNKNOWN = -1,
      FIF_BMP = 0,
      FIF_ICO = 1,
      FIF_JPEG = 2,
      FIF_JNG = 3,
      FIF_KOALA = 4,
      FIF_LBM = 5,
      FIF_IFF = FIF_LBM,
      FIF_MNG = 6,
      FIF_PBM = 7,
      FIF_PBMRAW = 8,
      FIF_PCD = 9,
      FIF_PCX = 10,
      FIF_PGM = 11,
      FIF_PGMRAW = 12,
      FIF_PNG = 13,
      FIF_PPM = 14,
      FIF_PPMRAW = 15,
      FIF_RAS = 16,
      FIF_TARGA = 17,
      FIF_TIFF = 18,
      FIF_WBMP = 19,
      FIF_PSD = 20,
      FIF_CUT = 21,
      FIF_XBM = 22,
      FIF_XPM = 23,
      FIF_DDS = 24,
      FIF_GIF = 25,
      FIF_HDR = 26,
      FIF_FAXG3 = 27,
      FIF_SGI = 28,
      FIF_EXR = 29,
      FIF_J2K = 30,
      FIF_JP2 = 31,
      FIF_PFM = 32,
      FIF_PICT = 33,
      FIF_RAW = 34,
      FIF_WEBP = 35,
      FIF_JXR = 36
    }

    const string FreeImageLibrary = "FreeImage";

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_GetFileTypeFromMemory")]
    internal static extern Format GetFileTypeFromMemory(IntPtr memory, int size);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_OpenMemory")]
    internal static extern IntPtr OpenMemory(IntPtr data, uint size_in_bytes);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_CloseMemory")]
    internal static extern IntPtr CloseMemory(IntPtr data);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_LoadFromMemory")]
    internal static extern IntPtr LoadFromMemory(Format format, IntPtr stream, int flags);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_Unload")]
    internal static extern void Unload(IntPtr dib);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_ConvertToRawBits")]
    internal static extern void ConvertToRawBits(IntPtr bits, IntPtr dib, int pitch, uint bpp, uint red_mask, uint green_mask, uint blue_mask, bool topdown);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_GetWidth")]
    internal static extern uint GetWidth(IntPtr handle);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_GetHeight")]
    internal static extern uint GetHeight(IntPtr handle);

    [DllImport(FreeImageLibrary, EntryPoint = "FreeImage_IsTransparent")]
    internal static extern bool IsTransparent(IntPtr dib);
  }
}
