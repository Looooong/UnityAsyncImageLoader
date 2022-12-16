public partial class AsyncImageLoader {
  interface IPixel<TChannel> where TChannel : struct {
    static int ChannelCount() => throw new System.NotImplementedException();
    static int ChannelByteCount() => throw new System.NotImplementedException();
  }

  struct RGB24Pixel : IPixel<byte> {
    public static int ChannelCount() => 3;
    public static int ChannelByteCount() => sizeof(byte);

    public byte r, g, b;
  }

  struct RGBA32Pixel : IPixel<byte> {
    public static int ChannelCount() => 4;
    public static int ChannelByteCount() => sizeof(byte);

    public byte r, g, b, a;
  }
}
