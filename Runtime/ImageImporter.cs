using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using static Unity.Mathematics.math;

public static partial class AsyncImageLoader {
  class ImageImporter : IDisposable {
    static readonly int MaxTextureSize = SystemInfo.maxTextureSize;
    static readonly int MaxMipmapLevel = (int)Mathf.Log(SystemInfo.maxTextureSize, 2f) + 1;
    static readonly ProfilerMarker ConstructorMarker = new ProfilerMarker("ImageImporter.Constructor");
    static readonly ProfilerMarker CreateNewTextureMarker = new ProfilerMarker("ImageImporter.CreateNewTexture");
    static readonly ProfilerMarker LoadIntoTextureMarker = new ProfilerMarker("ImageImporter.LoadIntoTexture");

    LoaderSettings _loaderSettings;

    IntPtr _bitmap;
    bool _isBitmapType;
    int _width;
    int _height;
    TextureFormat _textureFormat;
    int _bytesPerPixel;

    public ImageImporter(byte[] imageData, LoaderSettings loaderSettings) {
      using (ConstructorMarker.Auto()) {
        _loaderSettings = loaderSettings;
        _bitmap = IntPtr.Zero;

        IntPtr memoryStream = IntPtr.Zero;

        try {
          memoryStream = FreeImage.OpenMemory(
            Marshal.UnsafeAddrOfPinnedArrayElement(imageData, 0),
            (uint)imageData.Length
          );

          if (_loaderSettings.format == FreeImage.Format.FIF_UNKNOWN) {
            _loaderSettings.format = FreeImage.GetFileTypeFromMemory(memoryStream, imageData.Length);
          }

          if (_loaderSettings.format == FreeImage.Format.FIF_UNKNOWN) {
            throw new Exception("Cannot automatically determine the image format. Consider explicitly specifying image format.");
          }

          _bitmap = FreeImage.LoadFromMemory(_loaderSettings.format, memoryStream, (int)FreeImage.LoadFlags.JPEG_ACCURATE);
          _width = (int)FreeImage.GetWidth(_bitmap);
          _height = (int)FreeImage.GetHeight(_bitmap);

          if (_width > MaxTextureSize || _height > MaxTextureSize) {
            Dispose();
            throw new Exception("Texture size exceed maximum dimension supported by Unity.");
          }

          DetermineTextureFormat();
        } finally {
          if (memoryStream != IntPtr.Zero) FreeImage.CloseMemory(memoryStream);
        }
      }
    }

    public void Dispose() {
      if (_bitmap != IntPtr.Zero) {
        FreeImage.Unload(_bitmap);
        _bitmap = IntPtr.Zero;
      }
    }

    public Texture2D CreateTexture() {
      var mipmapCount = GetMipmapCount();
      return new Texture2D(_width, _height, _textureFormat, mipmapCount, _loaderSettings.linear);
    }

    public void ReinitializeTexture(Texture2D texture) {
#if UNITY_2021_1_OR_NEWER
      texture.Reinitialize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#else
      texture.Resize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#endif
    }

    public Texture2D CreateNewTexture() {
      using (CreateNewTextureMarker.Auto()) {
        var texture = CreateTexture();
        var dependency = LoadRawTextureData(texture);
        if (_isBitmapType) dependency = SwapColorChannel(texture, dependency);
        dependency = ScheduleMipmapJobForEachLevels(texture, dependency);
        dependency.Complete();
        texture.Apply(false, _loaderSettings.markNonReadable);
        return texture;
      }
    }

    public async Task<Texture2D> CreateNewTextureAsync() {
      var texture = CreateTexture();
      var dependency = LoadRawTextureData(texture);
      if (_isBitmapType) dependency = SwapColorChannel(texture, dependency);
      dependency = ScheduleMipmapJobForEachLevels(texture, dependency);
      while (!dependency.IsCompleted) await Task.Yield();
      dependency.Complete();
      texture.Apply(false, _loaderSettings.markNonReadable);
      return texture;
    }

    public void LoadIntoTexture(Texture2D texture) {
      using (LoadIntoTextureMarker.Auto()) {
        ReinitializeTexture(texture);
        var dependency = LoadRawTextureData(texture);
        if (_isBitmapType) dependency = SwapColorChannel(texture, dependency);
        dependency = ScheduleMipmapJobForEachLevels(texture, dependency);
        dependency.Complete();
        texture.Apply(false, _loaderSettings.markNonReadable);
      }
    }

    public async Task LoadIntoTextureAsync(Texture2D texture) {
      ReinitializeTexture(texture);
      var dependency = LoadRawTextureData(texture);
      if (_isBitmapType) dependency = SwapColorChannel(texture, dependency);
      dependency = ScheduleMipmapJobForEachLevels(texture, dependency);
      while (!dependency.IsCompleted) await Task.Yield();
      dependency.Complete();
      texture.Apply(false, _loaderSettings.markNonReadable);
    }

    int GetMipmapCount() {
      if (!_loaderSettings.generateMipmap) return 1;

      var maxDimension = Mathf.Max(_width, _height);
      var mipmapCount = Mathf.FloorToInt(Mathf.Log(maxDimension, 2f)) + 1;
      mipmapCount = Mathf.Clamp(mipmapCount, 2, MaxMipmapLevel);

      if (!_loaderSettings.autoMipmapCount) {
        mipmapCount = Mathf.Clamp(_loaderSettings.mipmapCount, 2, mipmapCount);
      }

      return mipmapCount;
    }

    int2 GetMipmapSize(int mipmapLevel) {
      // Base level
      if (mipmapLevel == 0) {
        return int2(_width, _height);
      } else {
        var mipmapFactor = Mathf.Pow(2f, -mipmapLevel);
        var mipmapWidth = Mathf.Max(1, Mathf.FloorToInt(mipmapFactor * _width));
        var mipmapHeight = Mathf.Max(1, Mathf.FloorToInt(mipmapFactor * _height));
        return int2(mipmapWidth, mipmapHeight);
      }
    }

    void DetermineTextureFormat() {
      var type = FreeImage.GetImageType(_bitmap);

      switch (type) {
        case FreeImage.Type.FIT_BITMAP:
          var bpp = FreeImage.GetBPP(_bitmap);

          _isBitmapType = true;

          switch (bpp) {
            case 24:
              _textureFormat = TextureFormat.RGB24;
              _bytesPerPixel = 3;
              break;
            case 32:
              _textureFormat = TextureFormat.RGBA32;
              _bytesPerPixel = 4;
              break;
            default:
              throw new Exception($"Bitmap bitdepth not supported: {bpp}");
          }
          break;
        default:
          throw new Exception(
            $"Image type not supported: {type}. Please submit a new GitHub issue if you want this format to be supported."
          );
      }
    }

    public JobHandle LoadRawTextureData(Texture2D texture, JobHandle dependency = default) {
      var textureData = GetTextureData(texture, 0);
      var loadRawTextureDataJob = new LoadRawTextureDataJob {
        width = _width,
        bytesPerPixel = _bytesPerPixel,
        bitmap = _bitmap,
        textureData = textureData,
      };

      return loadRawTextureDataJob.Schedule(dependency);
    }

    public JobHandle SwapColorChannel(Texture2D texture, JobHandle dependency = default) {
      var textureData = GetTextureData(texture, 0);
      var swapColorChannelJob = new SwapColorChannelJob {
        channelCount = _textureFormat == TextureFormat.RGBA32 ? RGBA32Pixel.ChannelCount() : RGB24Pixel.ChannelCount(),
        textureData = textureData,
      };

      return swapColorChannelJob.Schedule(_width * _height, min(_width, _height), dependency);
    }

    public JobHandle ScheduleMipmapJobForEachLevels(Texture2D texture, JobHandle dependency = default) {
      var mipmapSize = GetMipmapSize(0);
      var mipmapSlice = GetTextureData(texture, 0);

      for (int mipmapLevel = 1; mipmapLevel < texture.mipmapCount; mipmapLevel++) {
        var nextMipmapSize = GetMipmapSize(mipmapLevel);
        var nextMipmapSlice = GetTextureData(texture, mipmapLevel);

        switch (_textureFormat) {
          case TextureFormat.RGBA32:
            dependency = ScheduleMipmapJobForEachChannels<byte, RGBA32Pixel, ByteChannelMipmapJob>(
              RGBA32Pixel.ChannelCount(), RGBA32Pixel.ChannelByteCount(),
              mipmapSlice, mipmapSize,
              nextMipmapSlice, nextMipmapSize,
              dependency
            );
            break;
          case TextureFormat.RGB24:
            dependency = ScheduleMipmapJobForEachChannels<byte, RGB24Pixel, ByteChannelMipmapJob>(
              RGB24Pixel.ChannelCount(), RGB24Pixel.ChannelByteCount(),
              mipmapSlice, mipmapSize,
              nextMipmapSlice, nextMipmapSize,
              dependency
            );
            break;
          default:
            throw new NotSupportedException($"Texture format not supported: {_textureFormat}");
        }

        mipmapSize = nextMipmapSize;
        mipmapSlice = nextMipmapSlice;
      }

      return dependency;
    }

    JobHandle ScheduleMipmapJobForEachChannels<TChannel, TPixel, TMipmapJob>(
      int channelCount, int channelByteCount,
      NativeSlice<byte> inputSlice, int2 inputSize,
      NativeSlice<byte> outputSlice, int2 outputSize,
      JobHandle dependency
    )
      where TChannel : struct
      where TPixel : struct, IPixel<TChannel>
      where TMipmapJob : struct, IMipmapJob<TChannel> {
      var minSize = min(outputSize.x, outputSize.y);
      var inputPixelSlice = inputSlice.SliceConvert<TPixel>();
      var outputPixelSlice = outputSlice.SliceConvert<TPixel>();

      for (var channel = 0; channel < channelCount; channel++) {
        var mipmapJob = new TMipmapJob {
          InputSize = inputSize,
          OutputSize = outputSize,
          InputChannel = inputPixelSlice.SliceWithStride<TChannel>(channel * channelByteCount),
          OutputChannel = outputPixelSlice.SliceWithStride<TChannel>(channel * channelByteCount),
        };

        dependency = mipmapJob.Schedule(outputPixelSlice.Length, minSize, dependency);
      }

      return dependency;
    }

    NativeSlice<byte> GetTextureData(Texture2D texture, int mipmapLevel) {
#if UNITY_2020_1_OR_NEWER
      return texture.GetPixelData<byte>(mipmapLevel);
#endif

#pragma warning disable CS0162
      var rawTextureData = texture.GetRawTextureData<byte>();
      var mipmapByteSize = 0;
      var mipmapOffset = 0;

      for (var i = 0; i <= mipmapLevel; i++) {
        mipmapOffset += mipmapByteSize;
        var mipmapSize = GetMipmapSize(i);
        mipmapByteSize = _bytesPerPixel * mipmapSize.x * mipmapSize.y;
      }

      return new NativeSlice<byte>(rawTextureData, mipmapOffset, mipmapByteSize);
#pragma warning restore CS0162
    }

    struct LoadRawTextureDataJob : IJob {
      public int width;
      public int bytesPerPixel;

      [NativeDisableUnsafePtrRestriction]
      public IntPtr bitmap;

      [WriteOnly] public NativeSlice<byte> textureData;

      public unsafe void Execute() {
        FreeImage.ConvertToRawBits(
          (IntPtr)textureData.GetUnsafePtr(), bitmap,
          bytesPerPixel * width,
          (uint)(bytesPerPixel * 8),
          0, 0, 0, false
        );
      }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct SwapColorChannelJob : IJobParallelFor {
      public int channelCount;

      [NativeDisableParallelForRestriction]
      public NativeSlice<byte> textureData;

      public void Execute(int index) {
        var temp = textureData[mad(channelCount, index, 0)];
        textureData[mad(channelCount, index, 0)] = textureData[mad(channelCount, index, 2)];
        textureData[mad(channelCount, index, 2)] = temp;
      }
    }
  }
}
