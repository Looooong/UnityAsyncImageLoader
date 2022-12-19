using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Mathematics.math;

public partial class AsyncImageLoader {
  interface IMipmapJob<TChannel> : IJobParallelFor where TChannel : struct {
    int2 InputSize { get; set; }
    int2 OutputSize { get; set; }
    NativeSlice<TChannel> InputChannel { get; set; }
    NativeSlice<TChannel> OutputChannel { get; set; }
  }

  [BurstCompile(CompileSynchronously = true)]
  struct ByteChannelMipmapJob : IMipmapJob<byte> {
    public int2 InputSize { get; set; }
    public int2 OutputSize { get; set; }

    public NativeSlice<byte> InputChannel { get => _inputChannel; set => _inputChannel = value; }
    public NativeSlice<byte> OutputChannel { get => _outputChannel; set => _outputChannel = value; }

    [ReadOnly] NativeSlice<byte> _inputChannel;
    [WriteOnly] NativeSlice<byte> _outputChannel;

    public void Execute(int outputIndex) {
      var outputPosition = int2(
        outputIndex % OutputSize.x,
        outputIndex / OutputSize.x
      );
      var offset = int2(0);
      var total = 0u;

      for (offset.y = 0; offset.y < 2; offset.y++) {
        for (offset.x = 0; offset.x < 2; offset.x++) {
          var inputPosition = min(mad(2, outputPosition, offset), InputSize - 1);
          var inputIndex = mad(InputSize.x, inputPosition.y, inputPosition.x);
          total += _inputChannel[inputIndex];
        }
      }

      _outputChannel[outputIndex] = (byte)(total >> 2);
    }
  }

  [BurstCompile(CompileSynchronously = true)]
  struct ShortChannelMipmapJob : IMipmapJob<short> {
    public int2 InputSize { get; set; }
    public int2 OutputSize { get; set; }

    public NativeSlice<short> InputChannel { get => _inputChannel; set => _inputChannel = value; }
    public NativeSlice<short> OutputChannel { get => _outputChannel; set => _outputChannel = value; }

    [ReadOnly] NativeSlice<short> _inputChannel;
    [WriteOnly] NativeSlice<short> _outputChannel;

    public void Execute(int outputIndex) {
      var outputPosition = int2(
        outputIndex % OutputSize.x,
        outputIndex / OutputSize.x
      );
      var offset = int2(0);
      var total = 0;

      for (offset.y = 0; offset.y < 2; offset.y++) {
        for (offset.x = 0; offset.x < 2; offset.x++) {
          var inputPosition = min(mad(2, outputPosition, offset), InputSize - 1);
          var inputIndex = mad(InputSize.x, inputPosition.y, inputPosition.x);
          total += _inputChannel[inputIndex];
        }
      }

      _outputChannel[outputIndex] = (short)(total >> 2);
    }
  }

  [BurstCompile(CompileSynchronously = true)]
  struct FloatChannelMipmapJob : IMipmapJob<float> {
    public int2 InputSize { get; set; }
    public int2 OutputSize { get; set; }

    public NativeSlice<float> InputChannel { get => _inputChannel; set => _inputChannel = value; }
    public NativeSlice<float> OutputChannel { get => _outputChannel; set => _outputChannel = value; }

    [ReadOnly] NativeSlice<float> _inputChannel;
    [WriteOnly] NativeSlice<float> _outputChannel;

    public void Execute(int outputIndex) {
      var outputPosition = int2(
        outputIndex % OutputSize.x,
        outputIndex / OutputSize.x
      );
      var offset = int2(0);
      var total = 0f;

      for (offset.y = 0; offset.y < 2; offset.y++) {
        for (offset.x = 0; offset.x < 2; offset.x++) {
          var inputPosition = min(mad(2, outputPosition, offset), InputSize - 1);
          var inputIndex = mad(InputSize.x, inputPosition.y, inputPosition.x);
          total += _inputChannel[inputIndex];
        }
      }

      _outputChannel[outputIndex] = .25f * total;
    }
  }
}
