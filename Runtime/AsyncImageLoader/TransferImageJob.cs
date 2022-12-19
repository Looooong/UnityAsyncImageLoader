using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

public partial class AsyncImageLoader {
  [BurstCompile(CompileSynchronously = true)]
  struct TransferImageToTextureJob : IJobParallelFor {
    public int bytesPerLine;
    public int bytesPerScanline;

    [NativeDisableUnsafePtrRestriction]
    public System.IntPtr bitsPtr;

    [WriteOnly] public NativeSlice<byte> textureData;

    public unsafe void Execute(int rowIndex) {
      UnsafeUtility.MemCpy(
        (byte*)textureData.GetUnsafePtr() + rowIndex * bytesPerLine,
        (bitsPtr + rowIndex * bytesPerScanline).ToPointer(),
        bytesPerLine
      );
    }
  }

  [BurstCompile(CompileSynchronously = true)]
  struct TransferBGR24ImageToRGB24TextureJob : IJobParallelFor {
    public int bytesPerScanline;
    public int width;

    [NativeDisableUnsafePtrRestriction]
    public System.IntPtr bitsPtr;

    [WriteOnly] public NativeSlice<byte> textureData;

    public unsafe void Execute(int rowIndex) {
      var rowData = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<RGB24Pixel>(
        (RGB24Pixel*)textureData.GetUnsafePtr() + rowIndex * width, sizeof(RGB24Pixel), width
      );
      var rowBits = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<RGB24Pixel>(
        (bitsPtr + rowIndex * bytesPerScanline).ToPointer(), sizeof(RGB24Pixel), width
      );

      rowData.SliceWithStride<byte>(0).CopyFrom(rowBits.SliceWithStride<byte>(2));
      rowData.SliceWithStride<byte>(1).CopyFrom(rowBits.SliceWithStride<byte>(1));
      rowData.SliceWithStride<byte>(2).CopyFrom(rowBits.SliceWithStride<byte>(0));
    }
  }

  [BurstCompile(CompileSynchronously = true)]
  struct TransferBGRA32ImageToRGBA32TextureJob : IJobParallelFor {
    public int bytesPerScanline;
    public int width;

    [NativeDisableUnsafePtrRestriction]
    public System.IntPtr bitsPtr;

    [WriteOnly] public NativeSlice<byte> textureData;

    public unsafe void Execute(int rowIndex) {
      var rowData = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<RGBA32Pixel>(
        (RGBA32Pixel*)textureData.GetUnsafePtr() + rowIndex * width, sizeof(RGBA32Pixel), width
      );
      var rowBits = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<RGBA32Pixel>(
        (bitsPtr + rowIndex * bytesPerScanline).ToPointer(), sizeof(RGBA32Pixel), width
      );

      rowData.SliceWithStride<byte>(0).CopyFrom(rowBits.SliceWithStride<byte>(2));
      rowData.SliceWithStride<byte>(1).CopyFrom(rowBits.SliceWithStride<byte>(1));
      rowData.SliceWithStride<byte>(2).CopyFrom(rowBits.SliceWithStride<byte>(0));
      rowData.SliceWithStride<byte>(3).CopyFrom(rowBits.SliceWithStride<byte>(3));
    }
  }

  [BurstCompile(CompileSynchronously = true)]
  struct TransferRGBFloatImageToRGBAFloatTextureJob : IJobParallelFor {
    public int bytesPerScanline;
    public int width;

    [NativeDisableUnsafePtrRestriction]
    public System.IntPtr bitsPtr;

    [WriteOnly] public NativeSlice<byte> textureData;

    public unsafe void Execute(int rowIndex) {
      var rowData = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<RGBAFloatPixel>(
        (RGBAFloatPixel*)textureData.GetUnsafePtr() + rowIndex * width, sizeof(RGBAFloatPixel), width
      );
      var rowAlphaData = rowData.SliceWithStride<float>(3 * sizeof(float));

      UnsafeUtility.MemCpyStride(
        rowData.GetUnsafePtr(), rowData.Stride,
        (bitsPtr + rowIndex * bytesPerScanline).ToPointer(), 3 * sizeof(float),
        3 * sizeof(float), width
      );

      for (var i = 0; i < rowAlphaData.Length; i++) rowAlphaData[i] = 1f;
    }
  }
}
