package com.goping.media;

import com.goping.config.AppProperties;
import com.goping.error.ApiException;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;
import org.springframework.web.multipart.MultipartFile;

import javax.imageio.ImageIO;
import javax.imageio.ImageReader;
import javax.imageio.stream.ImageInputStream;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.Iterator;
import java.util.Locale;
import java.util.UUID;

/**
 * Медиафайлы на диске: UPLOAD_DIR/avatars/..., UPLOAD_DIR/voice/...
 */
@Component
public class MediaStore {

    public static final String MEDIA_PREFIX = "/media/avatars/";
    public static final String VOICE_PREFIX = "/media/voice/";
    private static final long MAX_VOICE_BYTES = 10L * 1024 * 1024;

    private final Path uploadRoot;
    private final Path avatarsDir;
    private final Path voiceDir;
    private final String publicBaseUrl;
    private final long maxBytes;

    public MediaStore(AppProperties properties) throws IOException {
        this.uploadRoot = Paths.get(properties.uploadDir()).toAbsolutePath().normalize();
        this.avatarsDir = uploadRoot.resolve("avatars");
        this.voiceDir = uploadRoot.resolve("voice");
        this.publicBaseUrl = properties.publicBaseUrl();
        this.maxBytes = properties.maxAvatarBytes();
        Files.createDirectories(avatarsDir);
        Files.createDirectories(voiceDir);
    }

    /** Публичный путь к корню файлов для WebMvcConfigurer. */
    public String resourceLocation() {
        return uploadRoot.toUri().toString();
    }

    /**
     * Проверяет и сохраняет изображение, возвращает относительный путь /media/avatars/....
     */
    public String saveAvatar(MultipartFile file) throws IOException {
        if (file == null || file.isEmpty()) {
            throw unsupported();
        }
        if (file.getSize() > maxBytes) {
            throw new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "AVATAR_TOO_LARGE", "Файл слишком большой");
        }

        byte[] bytes = file.getBytes();
        if (bytes.length == 0) {
            throw unsupported();
        }
        if (bytes.length > maxBytes) {
            throw new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "AVATAR_TOO_LARGE", "Файл слишком большой");
        }

        String extension = inspectImage(bytes);
        String fileName = UUID.randomUUID() + extension;
        Files.write(avatarsDir.resolve(fileName), bytes);
        return MEDIA_PREFIX + fileName;
    }

    /**
     * Сохраняет голосовое сообщение, возвращает относительный путь /media/voice/....
     */
    public String saveVoice(MultipartFile file) throws IOException {
        if (file == null || file.isEmpty()) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_VOICE", "Голосовое сообщение пустое");
        }
        if (file.getSize() > MAX_VOICE_BYTES) {
            throw new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "VOICE_TOO_LARGE", "Голосовое сообщение слишком большое");
        }
        byte[] bytes = file.getBytes();
        if (bytes.length == 0 || bytes.length > MAX_VOICE_BYTES) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_VOICE", "Голосовое сообщение пустое или слишком большое");
        }

        String name = file.getOriginalFilename() == null ? "" : file.getOriginalFilename().toLowerCase(Locale.ROOT);
        String contentType = file.getContentType() == null ? "" : file.getContentType().toLowerCase(Locale.ROOT);
        String ext = voiceExtension(name, contentType);
        if (ext == null) {
            throw new ApiException(HttpStatus.UNSUPPORTED_MEDIA_TYPE, "UNSUPPORTED_MEDIA_TYPE",
                    "Поддерживаются голосовые файлы webm, ogg, mp3, wav, m4a, mp4");
        }

        String fileName = UUID.randomUUID() + ext;
        Files.write(voiceDir.resolve(fileName), bytes);
        return VOICE_PREFIX + fileName;
    }

    public String url(String avatarPath, String remoteAvatarUrl) {
        if (avatarPath != null && !avatarPath.isEmpty()) {
            return publicBaseUrl + avatarPath;
        }
        if (remoteAvatarUrl == null || remoteAvatarUrl.isBlank()) {
            return null;
        }
        return remoteAvatarUrl;
    }

    public String publicUrl(String path) {
        if (path == null || path.isBlank()) return null;
        if (path.startsWith("http://") || path.startsWith("https://")) return path;
        return publicBaseUrl + path;
    }

    public void deleteVoice(String voicePath) {
        if (voicePath == null || !voicePath.startsWith(VOICE_PREFIX)) return;
        try {
            Files.deleteIfExists(voiceDir.resolve(Paths.get(voicePath).getFileName().toString()));
        } catch (IOException ignored) {
        }
    }

    private static String voiceExtension(String name, String contentType) {
        if (name.endsWith(".webm") || contentType.contains("webm")) return ".webm";
        if (name.endsWith(".ogg") || contentType.contains("ogg")) return ".ogg";
        if (name.endsWith(".mp3") || contentType.contains("mpeg") || contentType.contains("mp3")) return ".mp3";
        if (name.endsWith(".wav") || contentType.contains("wav")) return ".wav";
        if (name.endsWith(".m4a") || contentType.contains("mp4") || contentType.contains("m4a") || name.endsWith(".mp4")) return ".m4a";
        return null;
    }

    public void delete(String avatarPath) {
        if (avatarPath == null || !avatarPath.startsWith(MEDIA_PREFIX)) {
            return;
        }
        try {
            Files.deleteIfExists(avatarsDir.resolve(Paths.get(avatarPath).getFileName().toString()));
        } catch (IOException ignored) {
            // Старый файл не критичен: профиль уже обновлён.
        }
    }

    private String inspectImage(byte[] bytes) {
        try (ImageInputStream input = ImageIO.createImageInputStream(new ByteArrayInputStream(bytes))) {
            if (input == null) {
                throw unsupported();
            }
            Iterator<ImageReader> readers = ImageIO.getImageReaders(input);
            if (!readers.hasNext()) {
                throw unsupported();
            }
            ImageReader reader = readers.next();
            try {
                reader.setInput(input, true, true);
                int width = reader.getWidth(0);
                int height = reader.getHeight(0);
                String format = reader.getFormatName().toLowerCase(Locale.ROOT);

                String extension = switch (format) {
                    case "jpeg", "jpg" -> ".jpg";
                    case "png" -> ".png";
                    case "gif" -> ".gif";
                    default -> null;
                };
                if (extension == null) {
                    throw unsupported();
                }
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096) {
                    throw new ApiException(HttpStatus.UNSUPPORTED_MEDIA_TYPE, "UNSUPPORTED_MEDIA_TYPE",
                            "Изображение должно быть не больше 4096×4096");
                }
                return extension;
            } finally {
                reader.dispose();
            }
        } catch (IOException e) {
            throw unsupported();
        }
    }

    private ApiException unsupported() {
        return new ApiException(HttpStatus.UNSUPPORTED_MEDIA_TYPE, "UNSUPPORTED_MEDIA_TYPE",
                "Поддерживаются изображения JPEG, PNG или GIF");
    }
}
