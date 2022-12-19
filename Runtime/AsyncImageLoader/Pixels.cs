public partial class AsyncImageLoader {
  interface IPixel<TChannel> where TChannel : struct {
    static int ChannelCount() => throw new System.NotImplementedException();
    static int ChannelByteCount() => throw new System.NotImplementedException();
  }

  struct R16Pixel : IPixel<short> {
    public static int ChannelCount() => 1;
    public static int ChannelByteCount() => sizeof(short);

    public short r;
  }

  struct RFloatPixel : IPixel<float> {
    public static int ChannelCount() => 1;
    public static int ChannelByteCount() => sizeof(float);

    public float r;
  }

  struct RGB24Pixel : IPixel<byte> {
    public static int ChannelCount() => 3;
    public static int ChannelByteCount() => sizeof(byte);

    public byte r, g, b;
  }

  struct RGB48Pixel : IPixel<short> {
    public static int ChannelCount() => 3;
    public static int ChannelByteCount() => sizeof(short);

    public short r, g, b;
  }

  struct RGBA32Pixel : IPixel<byte> {
    public static int ChannelCount() => 4;
    public static int ChannelByteCount() => sizeof(byte);

    public byte r, g, b, a;
  }

  struct RGBA64Pixel : IPixel<short> {
    public static int ChannelCount() => 4;
    public static int ChannelByteCount() => sizeof(short);

    public short r, g, b, a;
  }

  struct RGBAFloatPixel : IPixel<float> {
    public static int ChannelCount() => 4;
    public static int ChannelByteCount() => sizeof(float);

    public float r, g, b, a;
  }
}
