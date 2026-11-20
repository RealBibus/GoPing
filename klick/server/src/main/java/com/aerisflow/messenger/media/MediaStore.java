package com.aerisflow.messenger.media;

import com.aerisflow.messenger.config.AppProperties;
import com.aerisflow.messenger.error.ApiException;
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
 * Аватарки на диске: UPLOAD_DIR/avatars/<uuid>.<ext>.
 */
@Component
public class MediaStore {

    public static final String MEDIA_PREFIX = "/media/avatars/";

    private final Path uploadRoot;
    private final Path avatarsDir;
    private final String publicBaseUrl;
    private final long maxBytes;

    public MediaStore(AppProperties properties) throws IOException {
        this.uploadRoot = Paths.get(properties.uploadDir()).toAbsolutePath().normalize();
        this.avatarsDir = uploadRoot.resolve("avatars");
        this.publicBaseUrl = properties.publicBaseUrl();
        this.maxBytes = properties.maxAvatarBytes();
        Files.createDirectories(avatarsDir);
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

    public String url(String avatarPath) {
        if (avatarPath == null || avatarPath.isEmpty()) {
            return null;
        }
        return publicBaseUrl + avatarPath;
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
