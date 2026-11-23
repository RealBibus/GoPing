package com.goping.media;

import com.goping.config.AppProperties;
import com.goping.config.LiveLimits;
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
import java.time.YearMonth;
import java.time.ZoneOffset;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;

/**
 * Медиафайлы на диске: UPLOAD_DIR/avatars/..., UPLOAD_DIR/voice/..., UPLOAD_DIR/media/YYYY/MM/...
 */
@Component
public class MediaStore {

    public static final String MEDIA_PREFIX = "/media/avatars/";
    public static final String VOICE_PREFIX = "/media/voice/";
    public static final String CHAT_MEDIA_PREFIX = "/media/media/";

    private static final Map<String, String> MEDIA_TYPES = mediaTypes();

    private final Path uploadRoot;
    private final Path avatarsDir;
    private final Path voiceDir;
    private final Path chatMediaDir;
    private final String publicBaseUrl;
    private final long maxBytes;
    private final LiveLimits limits;

    public record StoredMedia(String path, String fileName, long sizeBytes, String mime, Path absolutePath) {
    }

    public MediaStore(AppProperties properties, LiveLimits limits) throws IOException {
        this.uploadRoot = Paths.get(properties.uploadDir()).toAbsolutePath().normalize();
        this.avatarsDir = uploadRoot.resolve("avatars");
        this.voiceDir = uploadRoot.resolve("voice");
        this.chatMediaDir = uploadRoot.resolve("media");
        this.publicBaseUrl = properties.publicBaseUrl();
        this.maxBytes = properties.maxAvatarBytes();
        this.limits = limits;
        Files.createDirectories(avatarsDir);
        Files.createDirectories(voiceDir);
        Files.createDirectories(chatMediaDir);
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
        long fileLimit = limits.get().maxFileBytes();
        if (file.getSize() > fileLimit) {
            throw new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "VOICE_TOO_LARGE", "Голосовое сообщение слишком большое");
        }
        byte[] bytes = file.getBytes();
        if (bytes.length == 0 || bytes.length > fileLimit) {
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

    /** Stores chat media under media/YYYY/MM and returns its public path. */
    public StoredMedia saveMedia(MultipartFile file) throws IOException {
        if (file == null || file.isEmpty()) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_MEDIA", "Файл пустой");
        }
        long fileLimit = limits.get().maxFileBytes();
        if (file.getSize() > fileLimit) {
            throw new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "FILE_TOO_LARGE", "Файл слишком большой");
        }
        byte[] bytes = file.getBytes();
        if (bytes.length == 0) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_MEDIA", "Файл пустой");
        }
        if (bytes.length > fileLimit) {
            throw new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "FILE_TOO_LARGE", "Файл слишком большой");
        }

        String originalName = file.getOriginalFilename() == null ? "" : file.getOriginalFilename();
        String extension = safeExtension(originalName);
        String mime = MEDIA_TYPES.get(extension);
        if (mime == null) {
            throw new ApiException(HttpStatus.UNSUPPORTED_MEDIA_TYPE, "UNSUPPORTED_MEDIA_TYPE",
                    "Неподдерживаемое расширение файла");
        }

        YearMonth month = YearMonth.now(ZoneOffset.UTC);
        String folder = String.format(Locale.ROOT, "%04d/%02d", month.getYear(), month.getMonthValue());
        Path targetDir = chatMediaDir.resolve(folder.replace('/', java.io.File.separatorChar));
        Files.createDirectories(targetDir);
        String fileName = UUID.randomUUID() + "." + extension;
        Path target = targetDir.resolve(fileName);
        Files.write(target, bytes);
        return new StoredMedia(CHAT_MEDIA_PREFIX + folder + "/" + fileName,
                safeFileName(originalName), bytes.length, mime, target);
    }

    public void deleteMedia(StoredMedia media) {
        if (media == null) return;
        try {
            Files.deleteIfExists(media.absolutePath());
        } catch (IOException ignored) {
            // The insert already failed, so a leftover file is not critical.
        }
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

    private static String safeExtension(String rawName) {
        String name = safeFileName(rawName);
        int dot = name.lastIndexOf('.');
        if (dot < 0 || dot == name.length() - 1) {
            return "";
        }
        String extension = name.substring(dot + 1).toLowerCase(Locale.ROOT);
        return extension.matches("[a-z0-9]{1,10}") ? extension : "";
    }

    private static String safeFileName(String rawName) {
        String name = rawName == null ? "" : rawName.replace('\\', '/');
        int slash = name.lastIndexOf('/');
        if (slash >= 0) {
            name = name.substring(slash + 1);
        }
        StringBuilder safe = new StringBuilder();
        for (int i = 0; i < name.length() && safe.length() < 200; i++) {
            char c = name.charAt(i);
            if (Character.isISOControl(c) || c == '"' || c == '\\' || c == '/' || c == ':') {
                continue;
            }
            safe.append(c);
        }
        return safe.toString();
    }

    private static Map<String, String> mediaTypes() {
        Map<String, String> types = new LinkedHashMap<>();
        types.put("jpg", "image/jpeg");
        types.put("jpeg", "image/jpeg");
        types.put("png", "image/png");
        types.put("gif", "image/gif");
        types.put("webp", "image/webp");
        types.put("mp4", "video/mp4");
        types.put("webm", "video/webm");
        types.put("mov", "video/quicktime");
        types.put("mp3", "audio/mpeg");
        types.put("ogg", "audio/ogg");
        types.put("wav", "audio/wav");
        types.put("m4a", "audio/mp4");
        types.put("pdf", "application/pdf");
        types.put("txt", "text/plain");
        types.put("csv", "text/csv");
        types.put("json", "application/json");
        types.put("zip", "application/zip");
        types.put("7z", "application/x-7z-compressed");
        types.put("rar", "application/x-rar-compressed");
        types.put("doc", "application/msword");
        types.put("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        types.put("xls", "application/vnd.ms-excel");
        types.put("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        types.put("ppt", "application/vnd.ms-powerpoint");
        types.put("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation");
        return Map.copyOf(types);
    }

    private ApiException unsupported() {
        return new ApiException(HttpStatus.UNSUPPORTED_MEDIA_TYPE, "UNSUPPORTED_MEDIA_TYPE",
                "Поддерживаются изображения JPEG, PNG или GIF");
    }
}
