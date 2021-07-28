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
#if UNITY_2020_1_OR_NEWER
    // Maximum texture size supported is 16K
    const int MAX_TEXTURE_DIMENSION = 16384;
    const int MAX_MIPMAP_COUNT = 15;
#else
    // Maximum texture size supported is 8K
    const int MAX_TEXTURE_DIMENSION = 8192;
    const int MAX_MIPMAP_COUNT = 14;
#endif

    static readonly ProfilerMarker ConstructorMarker = new ProfilerMarker("ImageImporter.Constructor");
    static readonly ProfilerMarker CreateNewTextureMarker = new ProfilerMarker("ImageImporter.CreateNewTexture");
    static readonly ProfilerMarker LoadIntoTextureMarker = new ProfilerMarker("ImageImporter.LoadIntoTexture");
    static readonly ProfilerMarker LoadRawTextureDataMarker = new ProfilerMarker("ImageImporter.LoadRawTextureData");
    static readonly ProfilerMarker ProcessRawTextureDataMarker = new ProfilerMarker("ImageImporter.ProcessRawTextureData");

    LoaderSettings _loaderSettings;

    IntPtr _bitmap;
    int _width;
    int _height;
    bool _isTransparent;
    int _pixelSize; // In bytes

    JobHandle _finalJob;

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

          _bitmap = FreeImage.LoadFromMemory(_loaderSettings.format, memoryStream, 0);
          _width = (int)FreeImage.GetWidth(_bitmap);
          _height = (int)FreeImage.GetHeight(_bitmap);

          if (_width > MAX_TEXTURE_DIMENSION || _height > MAX_TEXTURE_DIMENSION) {
            Dispose();
            throw new Exception("Texture size exceed maximum dimension supported by Unity.");
          }

          _isTransparent = FreeImage.IsTransparent(_bitmap);
          _pixelSize = _isTransparent ? 4 : 3;
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

    public Texture2D CreateNewTexture() {
      using (CreateNewTextureMarker.Auto()) {
        var mipmapCount = CalculateMipmapCount();
        var texture = new Texture2D(
          _width, _height,
          _isTransparent ? TextureFormat.RGBA32 : TextureFormat.RGB24,
          mipmapCount, _loaderSettings.linear
        );
        var rawTextureView = texture.GetRawTextureData<byte>();
        LoadRawTextureData(rawTextureView);
        ProcessRawTextureData(rawTextureView, mipmapCount);
        _finalJob.Complete();
        texture.Apply(false, _loaderSettings.markNonReadable);
        return texture;
      }
    }

    public async Task<Texture2D> CreateNewTextureAsync() {
      var mipmapCount = CalculateMipmapCount();
      var texture = new Texture2D(
        _width, _height,
        _isTransparent ? TextureFormat.RGBA32 : TextureFormat.RGB24,
        mipmapCount, _loaderSettings.linear
      );
      var rawTextureView = texture.GetRawTextureData<byte>();
      await Task.Run(() => LoadRawTextureData(rawTextureView));
      ProcessRawTextureData(rawTextureView, mipmapCount);
      while (!_finalJob.IsCompleted) await Task.Yield();
      texture.Apply(false, _loaderSettings.markNonReadable);
      return texture;
    }

    public void LoadIntoTexture(Texture2D texture) {
      using (LoadIntoTextureMarker.Auto()) {
        var mipmapCount = CalculateMipmapCount(true);
        texture.Resize(
          _width, _height,
          _isTransparent ? TextureFormat.RGBA32 : TextureFormat.RGB24,
          _loaderSettings.generateMipmap
        );
        var rawTextureView = texture.GetRawTextureData<byte>();
        LoadRawTextureData(rawTextureView);
        ProcessRawTextureData(rawTextureView, mipmapCount);
        _finalJob.Complete();
        texture.Apply(false, _loaderSettings.markNonReadable);
      }
    }

    public async Task LoadIntoTextureAsync(Texture2D texture) {
      var mipmapCount = CalculateMipmapCount(true);
      texture.Resize(
        _width, _height,
        _isTransparent ? TextureFormat.RGBA32 : TextureFormat.RGB24,
        _loaderSettings.generateMipmap
      );
      var rawTextureView = texture.GetRawTextureData<byte>();
      await Task.Run(() => LoadRawTextureData(rawTextureView));
      ProcessRawTextureData(rawTextureView, mipmapCount);
      while (!_finalJob.IsCompleted) await Task.Yield();
      texture.Apply(false, _loaderSettings.markNonReadable);
    }

    int CalculateMipmapCount(bool forceAutoCount = false) {
      if (!_loaderSettings.generateMipmap) return 1;

      var maxDimension = Mathf.Max(_width, _height);
      var mipmapCount = Mathf.FloorToInt(Mathf.Log(maxDimension, 2f)) + 1;
      mipmapCount = Mathf.Clamp(mipmapCount, 2, MAX_MIPMAP_COUNT);

      if (!_loaderSettings.autoMipmapCount && !forceAutoCount) {
        mipmapCount = Mathf.Clamp(_loaderSettings.mipmapCount, 2, mipmapCount);
      }

      return mipmapCount;
    }

    Vector2Int CalculateMipmapDimensions(int mipmapLevel) {
      // Base level
      if (mipmapLevel == 0) {
        return new Vector2Int(_width, _height);
      } else {
        var mipmapFactor = Mathf.Pow(2f, -mipmapLevel);
        var mipmapWidth = Mathf.Max(1, Mathf.FloorToInt(mipmapFactor * _width));
        var mipmapHeight = Mathf.Max(1, Mathf.FloorToInt(mipmapFactor * _height));
        return new Vector2Int(mipmapWidth, mipmapHeight);
      }
    }

    void LoadRawTextureData(NativeArray<byte> rawTextureView) {
      using (LoadRawTextureDataMarker.Auto()) {
        var mipmapDimensions = CalculateMipmapDimensions(0);
        var mipmapSize = mipmapDimensions.x * mipmapDimensions.y;
        var mipmapSlice = new NativeSlice<byte>(rawTextureView, 0, _pixelSize * mipmapSize);

        unsafe {
          FreeImage.ConvertToRawBits(
            (IntPtr)mipmapSlice.GetUnsafePtr(), _bitmap,
            _pixelSize * mipmapDimensions.x,
            _isTransparent ? 32u : 24u,
            0, 0, 0, false
          );
        }
      }
    }

    void ProcessRawTextureData(NativeArray<byte> rawTextureView, int mipmapCount) {
      using (ProcessRawTextureDataMarker.Auto()) {
        var mipmapDimensions = CalculateMipmapDimensions(0);
        var mipmapSize = mipmapDimensions.x * mipmapDimensions.y;
        var mipmapSlice = new NativeSlice<byte>(rawTextureView, 0, _pixelSize * mipmapSize);
        var mipmapIndex = _pixelSize * mipmapSize;

        _finalJob = _isTransparent ?
          new SwapGBRA32ToRGBA32Job {
            mipmapSlice = mipmapSlice
          }.Schedule(mipmapSize, 8192) :
          new SwapGBR24ToRGB24Job {
            mipmapSlice = mipmapSlice
          }.Schedule(mipmapSize, 8192);

        for (int mipmapLevel = 1; mipmapLevel < mipmapCount; mipmapLevel++) {
          var nextMipmapDimensions = CalculateMipmapDimensions(mipmapLevel);
          mipmapSize = nextMipmapDimensions.x * nextMipmapDimensions.y;
          var nextMipmapSlice = new NativeSlice<byte>(rawTextureView, mipmapIndex, _pixelSize * mipmapSize);
          mipmapIndex += _pixelSize * mipmapSize;
          _finalJob = _isTransparent ?
            new FilterMipmapRGBA32Job {
              inputWidth = mipmapDimensions.x,
              inputHeight = mipmapDimensions.y,
              inputMipmap = mipmapSlice.SliceConvert<uint>(),
              outputWidth = nextMipmapDimensions.x,
              outputHeight = nextMipmapDimensions.y,
              outputMipmap = nextMipmapSlice.SliceConvert<uint>(),
            }.Schedule(mipmapSize, 1024, _finalJob) :
            new FilterMipmapRGB24Job {
              inputWidth = mipmapDimensions.x,
              inputHeight = mipmapDimensions.y,
              inputMipmap = mipmapSlice,
              outputWidth = nextMipmapDimensions.x,
              outputHeight = nextMipmapDimensions.y,
              outputMipmap = nextMipmapSlice,
            }.Schedule(mipmapSize, 1024, _finalJob);

          mipmapDimensions = nextMipmapDimensions;
          mipmapSlice = nextMipmapSlice;
        }
      }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct SwapGBR24ToRGB24Job : IJobParallelFor {
      [NativeDisableParallelForRestriction]
      public NativeSlice<byte> mipmapSlice;

      public void Execute(int index) {
        var temp = mipmapSlice[3 * index];
        mipmapSlice[3 * index] = mipmapSlice[3 * index + 2];
        mipmapSlice[3 * index + 2] = temp;
      }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct SwapGBRA32ToRGBA32Job : IJobParallelFor {
      [NativeDisableParallelForRestriction]
      public NativeSlice<byte> mipmapSlice;

      public void Execute(int index) {
        var temp = mipmapSlice[4 * index];
        mipmapSlice[4 * index] = mipmapSlice[4 * index + 2];
        mipmapSlice[4 * index + 2] = temp;
      }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct FilterMipmapRGB24Job : IJobParallelFor {
      public int inputWidth;
      public int inputHeight;
      [ReadOnly]
      public NativeSlice<byte> inputMipmap;

      public int outputWidth;
      public int outputHeight;
      [WriteOnly, NativeDisableParallelForRestriction]
      public NativeSlice<byte> outputMipmap;

      public void Execute(int index) {
        var outputX = index % outputWidth;
        var outputY = index / outputWidth;
        var outputColor = new uint3();

        for (int offsetX = 0; offsetX < 2; offsetX++) {
          for (int offsetY = 0; offsetY < 2; offsetY++) {
            var inputX = min(mad(2, outputX, offsetX), inputWidth);
            var inputY = min(mad(2, outputY, offsetY), inputHeight);
            var inputIndex = mad(inputWidth, inputY, inputX);
            outputColor.x += (uint)inputMipmap[mad(3, inputIndex, 0)] >> 2;
            outputColor.y += (uint)inputMipmap[mad(3, inputIndex, 1)] >> 2;
            outputColor.z += (uint)inputMipmap[mad(3, inputIndex, 2)] >> 2;
          }
        }

        outputMipmap[mad(3, index, 0)] = (byte)outputColor.x;
        outputMipmap[mad(3, index, 1)] = (byte)outputColor.y;
        outputMipmap[mad(3, index, 2)] = (byte)outputColor.z;
      }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct FilterMipmapRGBA32Job : IJobParallelFor {
      public int inputWidth;
      public int inputHeight;
      [ReadOnly]
      public NativeSlice<uint> inputMipmap;

      public int outputWidth;
      public int outputHeight;
      [WriteOnly, NativeDisableParallelForRestriction]
      public NativeSlice<uint> outputMipmap;

      public void Execute(int index) {
        var outputX = index % outputWidth;
        var outputY = index / outputWidth;
        var outputColor = new uint4();

        for (int offsetX = 0; offsetX < 2; offsetX++) {
          for (int offsetY = 0; offsetY < 2; offsetY++) {
            var inputX = min(outputX * 2 + offsetX, inputWidth);
            var inputY = min(outputY * 2 + offsetY, inputHeight);
            var inputColor = inputMipmap[inputY * inputWidth + inputX];
            outputColor.x += (inputColor & 0x000000FFu) >> 2;
            outputColor.y += (inputColor & 0x0000FF00u) >> 10;
            outputColor.z += (inputColor & 0x00FF0000u) >> 18;
            outputColor.w += (inputColor & 0xFF000000u) >> 26;
          }
        }

        outputMipmap[index] =
          (outputColor.x << 0) |
          (outputColor.y << 8) |
          (outputColor.z << 16) |
          (outputColor.w << 24);
      }
    }
  }
}
