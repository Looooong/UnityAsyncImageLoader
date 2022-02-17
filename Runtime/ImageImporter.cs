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
    TextureFormat _textureFormat;
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

    public Texture2D CreateNewTexture() {
      using (CreateNewTextureMarker.Auto()) {
        var mipmapCount = CalculateMipmapCount();
        var texture = new Texture2D(_width, _height, _textureFormat, mipmapCount, _loaderSettings.linear);
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
      var texture = new Texture2D(_width, _height, _textureFormat, mipmapCount, _loaderSettings.linear);
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
#if UNITY_2021_1_OR_NEWER
        texture.Reinitialize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#else
        texture.Resize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#endif
        var rawTextureView = texture.GetRawTextureData<byte>();
        LoadRawTextureData(rawTextureView);
        ProcessRawTextureData(rawTextureView, mipmapCount);
        _finalJob.Complete();
        texture.Apply(false, _loaderSettings.markNonReadable);
      }
    }

    public async Task LoadIntoTextureAsync(Texture2D texture) {
      var mipmapCount = CalculateMipmapCount(true);
#if UNITY_2021_1_OR_NEWER
      texture.Reinitialize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#else
      texture.Resize(_width, _height, _textureFormat, _loaderSettings.generateMipmap);
#endif
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

    int2 CalculateMipmapDimensions(int mipmapLevel) {
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

          switch (bpp) {
            case 24:
              _textureFormat = TextureFormat.RGB24;
              _pixelSize = 3;
              break;
            case 32:
              _textureFormat = TextureFormat.RGBA32;
              _pixelSize = 4;
              break;
            default:
              throw new Exception($"Bitmap bitdepth not supported: {bpp}");
          }
          break;
        case FreeImage.Type.FIT_RGB16:
        case FreeImage.Type.FIT_RGBF:
          _textureFormat = TextureFormat.RGB24;
          _pixelSize = 3;
          break;
        case FreeImage.Type.FIT_RGBA16:
        case FreeImage.Type.FIT_RGBAF:
          _textureFormat = TextureFormat.RGBA32;
          _pixelSize = 4;
          break;
        default:
          throw new Exception($"Image type not supported: {type}");
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
            _textureFormat == TextureFormat.RGBA32 ? 32u : 24u,
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

        _finalJob = new BGRToRGBJob {
          textureData = mipmapSlice,
          processFunction = _textureFormat == TextureFormat.RGBA32 ?
            BGRToRGBJob.BGRA32ToRGBA32FP : BGRToRGBJob.BGR24ToRGB24FP
        }.Schedule(mipmapSize, 8192);

        for (int mipmapLevel = 1; mipmapLevel < mipmapCount; mipmapLevel++) {
          var nextMipmapDimensions = CalculateMipmapDimensions(mipmapLevel);
          mipmapSize = nextMipmapDimensions.x * nextMipmapDimensions.y;
          var nextMipmapSlice = new NativeSlice<byte>(rawTextureView, mipmapIndex, _pixelSize * mipmapSize);
          mipmapIndex += _pixelSize * mipmapSize;

          _finalJob = new FilterMipmapJob {
            inputMipmap = mipmapSlice,
            inputDimensions = mipmapDimensions,
            outputMipmap = nextMipmapSlice,
            outputDimensions = nextMipmapDimensions,
            processFunction = _textureFormat == TextureFormat.RGBA32 ?
              FilterMipmapJob.FilterMipmapRGBA32FP : FilterMipmapJob.FilterMipmapRGB24FP
          }.Schedule(mipmapSize, 1024, _finalJob);

          mipmapDimensions = nextMipmapDimensions;
          mipmapSlice = nextMipmapSlice;
        }
      }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct BGRToRGBJob : IJobParallelFor {
      public delegate void BGRToRGBDelegate(ref NativeSlice<byte> textureData, int index);

      public static readonly FunctionPointer<BGRToRGBDelegate> BGR24ToRGB24FP = BurstCompiler.CompileFunctionPointer<BGRToRGBDelegate>(BGR24ToRGB24);
      public static readonly FunctionPointer<BGRToRGBDelegate> BGRA32ToRGBA32FP = BurstCompiler.CompileFunctionPointer<BGRToRGBDelegate>(BGRA32ToRGBA32);

      [BurstCompile(CompileSynchronously = true)]
      static void BGR24ToRGB24(ref NativeSlice<byte> textureData, int index) {
        var temp = textureData[mad(3, index, 0)];
        textureData[mad(3, index, 0)] = textureData[mad(3, index, 2)];
        textureData[mad(3, index, 2)] = temp;
      }

      [BurstCompile(CompileSynchronously = true)]
      static void BGRA32ToRGBA32(ref NativeSlice<byte> textureData, int index) {
        var temp = textureData[mad(4, index, 0)];
        textureData[mad(4, index, 0)] = textureData[mad(4, index, 2)];
        textureData[mad(4, index, 2)] = temp;
      }

      [NativeDisableParallelForRestriction]
      public NativeSlice<byte> textureData;
      public FunctionPointer<BGRToRGBDelegate> processFunction;

      public void Execute(int index) => processFunction.Invoke(ref textureData, index);
    }

    [BurstCompile(CompileSynchronously = true)]
    struct FilterMipmapJob : IJobParallelFor {
      public delegate void FilterMipmapDelegate(ref FilterMipmapJob job, int outputIndex);

      public static readonly FunctionPointer<FilterMipmapDelegate> FilterMipmapRGB24FP = BurstCompiler.CompileFunctionPointer<FilterMipmapDelegate>(FilterMipmapRGB24);
      public static readonly FunctionPointer<FilterMipmapDelegate> FilterMipmapRGBA32FP = BurstCompiler.CompileFunctionPointer<FilterMipmapDelegate>(FilterMipmapRGBA32);

      [BurstCompile(CompileSynchronously = true)]
      static void FilterMipmapRGB24(ref FilterMipmapJob job, int outputIndex) {
        var outputX = outputIndex % job.outputDimensions.x;
        var outputY = outputIndex / job.outputDimensions.x;
        var outputColor = new uint3();

        for (var offsetY = 0; offsetY < 2; offsetY++) {
          for (var offsetX = 0; offsetX < 2; offsetX++) {
            var inputX = min(mad(2, outputX, offsetX), job.inputDimensions.x - 1);
            var inputY = min(mad(2, outputY, offsetY), job.inputDimensions.y - 1);
            var inputIndex = mad(job.inputDimensions.x, inputY, inputX);
            outputColor.x += (uint)job.inputMipmap[mad(3, inputIndex, 0)] >> 2;
            outputColor.y += (uint)job.inputMipmap[mad(3, inputIndex, 1)] >> 2;
            outputColor.z += (uint)job.inputMipmap[mad(3, inputIndex, 2)] >> 2;
          }
        }

        job.outputMipmap[mad(3, outputIndex, 0)] = (byte)outputColor.x;
        job.outputMipmap[mad(3, outputIndex, 1)] = (byte)outputColor.y;
        job.outputMipmap[mad(3, outputIndex, 2)] = (byte)outputColor.z;
      }

      [BurstCompile(CompileSynchronously = true)]
      static void FilterMipmapRGBA32(ref FilterMipmapJob job, int outputIndex) {
        var outputX = outputIndex % job.outputDimensions.x;
        var outputY = outputIndex / job.outputDimensions.x;
        var outputColor = new uint4();

        for (var offsetY = 0; offsetY < 2; offsetY++) {
          for (var offsetX = 0; offsetX < 2; offsetX++) {
            var inputX = min(mad(2, outputX, offsetX), job.inputDimensions.x - 1);
            var inputY = min(mad(2, outputY, offsetY), job.inputDimensions.y - 1);
            var inputIndex = mad(job.inputDimensions.x, inputY, inputX);
            outputColor.x += (uint)job.inputMipmap[mad(4, inputIndex, 0)] >> 2;
            outputColor.y += (uint)job.inputMipmap[mad(4, inputIndex, 1)] >> 2;
            outputColor.z += (uint)job.inputMipmap[mad(4, inputIndex, 2)] >> 2;
            outputColor.w += (uint)job.inputMipmap[mad(4, inputIndex, 3)] >> 2;
          }
        }

        job.outputMipmap[mad(4, outputIndex, 0)] = (byte)outputColor.x;
        job.outputMipmap[mad(4, outputIndex, 1)] = (byte)outputColor.y;
        job.outputMipmap[mad(4, outputIndex, 2)] = (byte)outputColor.z;
        job.outputMipmap[mad(4, outputIndex, 3)] = (byte)outputColor.w;
      }

      [ReadOnly]
      public NativeSlice<byte> inputMipmap;
      public int2 inputDimensions;

      [WriteOnly, NativeDisableContainerSafetyRestriction, NativeDisableParallelForRestriction]
      public NativeSlice<byte> outputMipmap;
      public int2 outputDimensions;

      public FunctionPointer<FilterMipmapDelegate> processFunction;

      public void Execute(int outputIndex) => processFunction.Invoke(ref this, outputIndex);
    }
  }
}
