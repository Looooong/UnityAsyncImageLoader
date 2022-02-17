## [Unreleased]

## [0.1.2] - 2022-02-17
### Fixed
- Fix Burst complain about two containers maybe aliasing in FilterMipmapJob.
- Use `Texture2D.Reinitialize` instead of `Texture2D.Resize` from Unity 2021.1 onward.

## [0.1.1] - 2021-07-30
### Added
- Add `CHANGELOG.md`.

### Changed
- Refactor Burst jobs.

### Fixed
- Fix typo in `README.md`.
- Fix performance issue when importing PNG without alpha channel.
- Fix out of bound access in mipmap generation job.

## [0.1.0] - 2021-07-28
### Added
- Implement `AsyncImageLoader`.

[Unreleased]: https://github.com/Looooong/UnityAsyncImageLoader/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/Looooong/UnityAsyncImageLoader/releases/tag/v0.1.2
[0.1.1]: https://github.com/Looooong/UnityAsyncImageLoader/releases/tag/v0.1.1
[0.1.0]: https://github.com/Looooong/UnityAsyncImageLoader/releases/tag/v0.1.0
