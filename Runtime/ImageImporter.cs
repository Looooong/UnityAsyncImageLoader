using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using static Unity.Mathematics.math;

public static partial class AsyncImageLoader {
  static readonly int MaxTextureSize = SystemInfo.maxTextureSize;
  static readonly int MaxMipmapLevel = (int)Mathf.Log(SystemInfo.maxTextureSize, 2f) + 1;

  class ImageImporter : IDisposable {
    static readonly ProfilerMarker ConstructorMarker = new ProfilerMarker("ImageImporter.Constructor");
    static readonly ProfilerMarker CreateNewTextureMarker = new ProfilerMarker("ImageImporter.CreateNewTexture");
    static readonly ProfilerMarker LoadIntoTextureMarker = new ProfilerMarker("ImageImporter.LoadIntoTexture");

    LoaderSettings _loaderSettings;

    IntPtr _bitmap;
    int _width;
    int _height;
    TextureFormat _textureFormat;
    int _bytesPerPixel;
    int _imageBitsPerPixel;
    FreeImage.Type _imageType;

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
          _imageBitsPerPixel = FreeImage.GetBPP(_bitmap);
          _imageType = FreeImage.GetImageType(_bitmap);

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
      if ((texture.width, texture.height, texture.format, texture.mipmapCount > 1) != (_width, _height, _textureFormat, _loaderSettings.generateMipmap)) {
#if UNITY_2021_1_OR_NEWER
        texture.Reinitialize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#else
        texture.Resize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#endif
      }
    }

    public Texture2D CreateNewTexture() {
      using (CreateNewTextureMarker.Auto()) {
        var texture = CreateTexture();
        var dependency = ProcessImage(texture);
        dependency.Complete();
        texture.Apply(false, _loaderSettings.markNonReadable);
        return texture;
      }
    }

    public async Task<Texture2D> CreateNewTextureAsync() {
      var texture = CreateTexture();
      var dependency = ProcessImage(texture);
      while (!dependency.IsCompleted) await Task.Yield();
      dependency.Complete();
      texture.Apply(false, _loaderSettings.markNonReadable);
      return texture;
    }

    public void LoadIntoTexture(Texture2D texture) {
      using (LoadIntoTextureMarker.Auto()) {
        ReinitializeTexture(texture);
        var dependency = ProcessImage(texture);
        dependency.Complete();
        texture.Apply(false, _loaderSettings.markNonReadable);
      }
    }

    public async Task LoadIntoTextureAsync(Texture2D texture) {
      ReinitializeTexture(texture);
      var dependency = ProcessImage(texture);
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
      switch (_imageType) {
        case FreeImage.Type.FIT_BITMAP:
          switch (_imageBitsPerPixel) {
            case 24:
              _textureFormat = TextureFormat.RGB24;
              _bytesPerPixel = 3;
              break;
            case 32:
              _textureFormat = TextureFormat.RGBA32;
              _bytesPerPixel = 4;
              break;
            default:
              throw new Exception($"Bitmap bitdepth not supported: {_imageBitsPerPixel}");
          }
          break;
        case FreeImage.Type.FIT_INT16:
        case FreeImage.Type.FIT_UINT16:
          _textureFormat = TextureFormat.R16;
          _bytesPerPixel = 2;
          break;
        case FreeImage.Type.FIT_FLOAT:
          _textureFormat = TextureFormat.RFloat;
          _bytesPerPixel = 4;
          break;
        case FreeImage.Type.FIT_RGB16:
          _textureFormat = TextureFormat.RGB48;
          _bytesPerPixel = 6;
          break;
        case FreeImage.Type.FIT_RGBA16:
          _textureFormat = TextureFormat.RGBA64;
          _bytesPerPixel = 8;
          break;
        case FreeImage.Type.FIT_RGBF:
        case FreeImage.Type.FIT_RGBAF:
          _textureFormat = TextureFormat.RGBAFloat;
          _bytesPerPixel = 16;
          break;
        default:
          throw new Exception(
            $"Image type not supported: {_imageType}. Please submit a new GitHub issue if you want this type to be supported."
          );
      }
    }

    public JobHandle ProcessImage(Texture2D texture) {
      JobHandle dependency = default;
      dependency = ScheduleLoadTextureDataJob(texture, dependency);
      dependency = ScheduleMipmapJobForEachLevels(texture, dependency);
      return dependency;
    }

    public unsafe JobHandle ScheduleLoadTextureDataJob(Texture2D texture, JobHandle dependency = default) {
      var textureData = GetTextureData(texture, 0);
      var bitsPtr = FreeImage.GetBits(_bitmap);
      var bytesPerLine = FreeImage.GetLine(_bitmap);
      var bytesPerScanline = FreeImage.GetPitch(_bitmap);

      if (FreeImage.IsLittleEndian() && _imageType == FreeImage.Type.FIT_BITMAP) {
        switch (_imageBitsPerPixel) {
          case 24:
            var transferBGR24ImageToRGB24TextureJob = new TransferBGR24ImageToRGB24TextureJob {
              bytesPerScanline = bytesPerScanline,
              width = _width,
              bitsPtr = bitsPtr,
              textureData = textureData,
            };

            return transferBGR24ImageToRGB24TextureJob.Schedule(_height, 1, dependency);
          case 32:
            var transferBGRA32ImageToRGBA32TextureJob = new TransferBGRA32ImageToRGBA32TextureJob {
              bytesPerScanline = bytesPerScanline,
              width = _width,
              bitsPtr = bitsPtr,
              textureData = textureData,
            };

            return transferBGRA32ImageToRGBA32TextureJob.Schedule(_height, 1, dependency);
          default:
            throw new Exception($"Bitmap bitdepth not supported: {_imageBitsPerPixel}");
        }
      } else if (_imageType == FreeImage.Type.FIT_RGBF) {
        var transferRGBFloatImageToRGBAFloatTextureJob = new TransferRGBFloatImageToRGBAFloatTextureJob {
          bytesPerScanline = bytesPerScanline,
          width = _width,
          bitsPtr = bitsPtr,
          textureData = textureData,
        };

        return transferRGBFloatImageToRGBAFloatTextureJob.Schedule(_height, 1, dependency);
      } else {
        var transferImageToTextureJob = new TransferImageToTextureJob {
          bytesPerLine = bytesPerLine,
          bytesPerScanline = bytesPerScanline,
          bitsPtr = bitsPtr,
          textureData = textureData,
        };

        return transferImageToTextureJob.Schedule(_height, 1, dependency);
      }
    }

    public JobHandle ScheduleMipmapJobForEachLevels(Texture2D texture, JobHandle dependency = default) {
      var mipmapSize = GetMipmapSize(0);
      var mipmapSlice = GetTextureData(texture, 0);

      for (int mipmapLevel = 1; mipmapLevel < texture.mipmapCount; mipmapLevel++) {
        var nextMipmapSize = GetMipmapSize(mipmapLevel);
        var nextMipmapSlice = GetTextureData(texture, mipmapLevel);

        dependency = _textureFormat switch {
          TextureFormat.R16 => ScheduleMipmapJobForEachChannels<short, R16Pixel, ShortChannelMipmapJob>(
            R16Pixel.ChannelCount(), R16Pixel.ChannelByteCount(),
            mipmapSlice, mipmapSize, nextMipmapSlice, nextMipmapSize,
            dependency
          ),
          TextureFormat.RFloat => ScheduleMipmapJobForEachChannels<float, RFloatPixel, FloatChannelMipmapJob>(
            RFloatPixel.ChannelCount(), RFloatPixel.ChannelByteCount(),
            mipmapSlice, mipmapSize, nextMipmapSlice, nextMipmapSize,
            dependency
          ),
          TextureFormat.RGB24 => ScheduleMipmapJobForEachChannels<byte, RGB24Pixel, ByteChannelMipmapJob>(
              RGB24Pixel.ChannelCount(), RGB24Pixel.ChannelByteCount(),
              mipmapSlice, mipmapSize, nextMipmapSlice, nextMipmapSize,
              dependency
            ),
          TextureFormat.RGB48 => ScheduleMipmapJobForEachChannels<short, RGB48Pixel, ShortChannelMipmapJob>(
            RGB48Pixel.ChannelCount(), RGB48Pixel.ChannelByteCount(),
            mipmapSlice, mipmapSize, nextMipmapSlice, nextMipmapSize,
            dependency
          ),
          TextureFormat.RGBA32 => ScheduleMipmapJobForEachChannels<byte, RGBA32Pixel, ByteChannelMipmapJob>(
            RGBA32Pixel.ChannelCount(), RGBA32Pixel.ChannelByteCount(),
            mipmapSlice, mipmapSize, nextMipmapSlice, nextMipmapSize,
            dependency
          ),
          TextureFormat.RGBA64 => ScheduleMipmapJobForEachChannels<short, RGBA64Pixel, ShortChannelMipmapJob>(
            RGBA64Pixel.ChannelCount(), RGBA64Pixel.ChannelByteCount(),
            mipmapSlice, mipmapSize, nextMipmapSlice, nextMipmapSize,
            dependency
          ),
          TextureFormat.RGBAFloat => ScheduleMipmapJobForEachChannels<float, RGBAFloatPixel, FloatChannelMipmapJob>(
            RGBAFloatPixel.ChannelCount(), RGBAFloatPixel.ChannelByteCount(),
            mipmapSlice, mipmapSize, nextMipmapSlice, nextMipmapSize,
            dependency
          ),
          _ => throw new NotSupportedException($"Texture format not supported: {_textureFormat}"),
        };

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
  }
}
