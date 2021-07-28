# Unity Asynchronous Image Loader

[`ImageConversion.LoadImage`](https://docs.unity3d.com/ScriptReference/ImageConversion.LoadImage.html) and `Texture2D.LoadImage` are slow when loading large images (greater than 2K) at runtime. They blocks the Unity main thread when loading the image for a duration between a hundred milliseconds and even a few seconds. This is a dealbreaker for those games and applications that want to load those images programmatically at runtime.

This package aims to offload image loading, image decoding and mipmap generation to other threads. It creates smoother gameplay and reduces lag spike on the Unity main thread when loading large images.

This package uses [FreeImage](https://freeimage.sourceforge.io/). which is the same library used by Unity to process image data.

## Unity Version

This package is developed in Unity 2019.1. It may work on Unity 2020 and Unity 2021.

## Installation

The package can be installed using the Git URL `https://github.com/Looooong/UnityAsyncImageLoader.git` by following [Installing from a Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html) instructions.

## Dependencies

+ Unity Burst
+ Unity Mathematics

## Usage

### Loader Settings

```cs
  /// <summary>Settings used by the image loader.</summary>
  public struct LoaderSettings {
    /// <summary>Create linear texture. Only applicable to methods that create new <c>Texture2D</c>. Defaults to false.</summary>
    public bool linear;
    /// <summary>Texture data won't be readable on the CPU after loading. Defaults to false.</summary>
    public bool markNonReadable;
    /// <summary>Whether or not to generate mipmaps. Defaults to true.</summary>
    public bool generateMipmap;
    /// <summary>Automatically calculate the number of mipmap levels. Defaults to true. Only applicable to methods that create new <c>Texture2D</c>.</summary>
    public bool autoMipmapCount;
    /// <summary>Mipmap count, including the base level. Must be greater than 1. Only applicable to methods that create new <c>Texture2D</c>.</summary>
    public int mipmapCount;
    /// <summary>Used to explicitly specify the image format. Defaults to FIF_UNKNOWN, which the image format will be automatically determined.</summary>
    public FreeImage.Format format;
    /// <summary>Whether or not to log exception caught by this method. Defaults to true.</summary>
    public bool logException;

    public static LoaderSettings Default => new LoaderSettings {
      linear = false,
      markNonReadable = false,
      generateMipmap = true,
      autoMipmapCount = true,
      format = FreeImage.Format.FIF_UNKNOWN,
      logException = true,
    };
  }
```

### Load Image Asynchronously

```cs
  var imageData = File.ReadAllBytes();
  var texture = new Texture2D(1, 1);
  var loaderSettings = AsyncImageLoader.LoaderSettings.Default;
  var success = false;

  // =====================================
  // Load image data into existing texture
  // =====================================

  // Use the default LoaderSettings
  success = await AsyncImageLoader.LoadImageAsync(texture, imageData);

  // Similar to ImageConversion.LoadImage
  // Mark texture as unreadable after reading.
  success = await AsyncImageLoader.LoadImageAsync(texture, imageData, true);

  // Use a custom LoaderSettings
  success = await AsyncImageLoader.LoadImageAsync(texture, imageData, loaderSettings);

  // ==================================
  // Create new texture from image data
  // ==================================

  // Use the default LoaderSettings
  texture = await AsyncImageLoader.CreateFromImageAsync(imageData);

  // Use a custom LoaderSettings
  texture = await AsyncImageLoader.CreateFromImageAsync(imageData, loaderSettings);
```

### Load Image Synchronously

The synchronous variants are the same as the  asynchronous counterparts but without `Async` suffix in theirs name. They are useful for debugging and profiling within a frame.

```cs
  var imageData = File.ReadAllBytes();
  var texture = new Texture2D(1, 1);
  var loaderSettings = AsyncImageLoader.LoaderSettings.Default;
  var success = false;

  // =====================================
  // Load image data into existing texture
  // =====================================

  // Use the default LoaderSettings
  success = AsyncImageLoader.LoadImage(texture, imageData);

  // Similar to ImageConversion.LoadImage
  // Mark texture as unreadable after reading.
  success = AsyncImageLoader.LoadImage(texture, imageData, true);

  // Use a custom LoaderSettings
  success = AsyncImageLoader.LoadImage(texture, imageData, loaderSettings);

  // ==================================
  // Create new texture from image data
  // ==================================

  // Use the default LoaderSettings
  texture = AsyncImageLoader.CreateFromImage(imageData);

  // Use a custom LoaderSettings
  texture = AsyncImageLoader.CreateFromImage(imageData, loaderSettings);
```

## After Loading

### Texture Format

If the image has alpha channel, the format will be `RGBA32`, otherwise, it will be `RGB24`.

### Mipmap Count

If the `LoadImage` and `LoadImageAsync` variants are used with `generateMipmap` set to `true`, the mipmap count is set to the maximum possible number for a particular texture. If you want to control the number of mipmap, you can use the `CreateFromImage` and `CreateFromImageAsync` instead.

### Mipmap Data

The mipmaps are generated using box filtering with 2x2 kernel. The final result won't be the same as Unity's counterpart when using texture import in the editor.

## Troubleshooting

### There is still lag spike when loading large images

After `AsyncImageLoader` method finishes executing, the image data are still transfering to the GPU. Therefore, any object, like material or UI, that wants to use the texture afterward will have to wait for the texture to finish uploading and thus block the Unity main thread.

There is no easy way to detect if the texture has finished uploading its data. The workarounds are:
+ Wait for a second or more before using the texture.
+ **(Not tested)** Use [`AsyncGPUReadback`](https://docs.unity3d.com/ScriptReference/Rendering.AsyncGPUReadback.html) to request a single pixel from the texture. It will wait for the texture to finish uploading before downloading that single pixel. Then the request callback can be used to notify the Unity main thread about the texture upload completion.

## Acknowledgement

This package is inspired by Matias Lavik's [`unity-async-textureimport`](https://codeberg.org/matiaslavik/unity-async-textureimport).
