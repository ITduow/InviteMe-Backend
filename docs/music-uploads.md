# Admin music uploads

The admin music form accepts MP3 files rather than external page/audio URLs. Existing library URLs and published invitation music snapshots remain readable. No database migration is required: `music_tracks.audio_url` holds the generated playback URL, and the file lives in media storage.

## API

- `POST /api/admin/music-tracks`: multipart form fields `name` and `file` (required).
- `PUT /api/admin/music-tracks/{id}`: multipart fields `name`, `expectedVersion`, and optional replacement `file`. Omitting `file` retains the existing audio.
- `PUT /api/admin/music-tracks/{id}/status`: unchanged JSON status/version contract.
- `GET /media/music/{key}`: anonymous playback, `audio/mpeg`, byte ranges, immutable cache headers and `nosniff`.

Both write routes require Platform Admin and `ADMIN_MUSIC_MANAGE`. POST/PUT no longer accept the previous JSON URL payload; deploy the matching FE and BE together. New tracks start hidden. Uploads are limited to 20 MiB plus bounded multipart overhead and ten writes per minute per admin (shared with the existing cover upload limiter).

The server checks extension, bounded content size, optional ID3 metadata and consecutive MPEG Layer III frames. HTML renamed to `.mp3`, metadata without audio and truncated frames are rejected; MIME declarations are not trusted. This validates the container/frame structure, not every decoded audio sample.

## Storage configuration

Music reuses `WeddingMedia:Provider`, `WeddingMedia:PublicBaseUrl`, local directory and S3 credentials/bucket/region. It has its own size validation, independent of the 5 MiB image setting.

- `WeddingMedia__PublicBaseUrl`: externally reachable **API** base URL, such as `https://api.example.com`. HTTPS is required; HTTP loopback is accepted for local development. Configure this before uploading.
- Local: files are stored beneath `WeddingMedia__LocalDirectory/music`. Mount that directory on persistent storage.
- S3: files use `{WeddingMedia:S3KeyPrefix}/music/{random-key}.mp3`. The bucket can remain private; the API reads objects using the existing signed S3 client.
- Reverse proxies must allow at least 21 MiB request bodies on the admin upload routes and route `/media/music/` to the API.

Object keys are random and immutable. A failed database save triggers cleanup of the new object. Replacement does **not** delete the previous file because published invitations can reference its URL. Hiding music also preserves existing playback. Future garbage collection must check published references before deleting retained files.

The admin UI supports file preview before saving, rename without replacing, optional replacement, and existing hide/show controls. Upload uses the shared authenticated API client with pending feedback; it does not report a fabricated upload percentage.
