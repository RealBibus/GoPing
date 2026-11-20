package com.aerisflow.messenger;

import com.fasterxml.jackson.databind.JsonNode;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.MethodOrderer;
import org.junit.jupiter.api.Order;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.TestMethodOrder;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.web.client.TestRestTemplate;
import org.springframework.core.io.ByteArrayResource;
import org.springframework.http.HttpEntity;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpMethod;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.http.client.JdkClientHttpRequestFactory;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;
import org.springframework.util.LinkedMultiValueMap;
import org.springframework.util.MultiValueMap;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Base64;
import java.util.UUID;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * Сквозные тесты API: настоящий HTTP, настоящая SQLite во временной папке.
 */
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
@TestMethodOrder(MethodOrderer.OrderAnnotation.class)
class ApiIntegrationTest {

    private static final String PUBLIC_BASE = "http://cdn.example.test";
    private static final Path ROOT = createRoot();

    private static final byte[] PNG = Base64.getDecoder().decode(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    @Autowired
    private TestRestTemplate rest;

    @BeforeEach
    void useJdkHttpClient() {
        // HttpURLConnection (по умолчанию) не умеет PATCH и ломается на 401.
        rest.getRestTemplate().setRequestFactory(new JdkClientHttpRequestFactory());
    }

    @DynamicPropertySource
    static void properties(DynamicPropertyRegistry registry) {
        registry.add("app.database-path", () -> ROOT.resolve("test.db").toString());
        registry.add("app.upload-dir", () -> ROOT.resolve("uploads").toString());
        registry.add("app.jwt-secret", () -> "integration-test-secret-0123456789abcdef0123456789abcdef");
        registry.add("app.public-base-url", () -> PUBLIC_BASE);
        registry.add("app.cors-origins", () -> "*");
        registry.add("app.auth-rate-limit", () -> "1000");
        registry.add("app.availability-rate-limit", () -> "1000");
    }

    private static Path createRoot() {
        try {
            return Files.createTempDirectory("aerisflow-it");
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    private String suffix() {
        return UUID.randomUUID().toString().substring(0, 8);
    }

    private JsonNode post(String path, Object body) {
        return rest.postForEntity(path, body, JsonNode.class).getBody();
    }

    private ResponseEntity<JsonNode> authed(HttpMethod method, String path, String token, Object body) {
        HttpHeaders headers = new HttpHeaders();
        headers.setBearerAuth(token);
        if (body != null) {
            headers.setContentType(MediaType.APPLICATION_JSON);
        }
        return rest.exchange(path, method, new HttpEntity<>(body, headers), JsonNode.class);
    }

    private AuthData register(String username, String email, String nickname) {
        String json = """
                {"email":"%s","username":"%s","password":"password123","nickname":"%s"}
                """.formatted(email, username, nickname);
        ResponseEntity<JsonNode> response = rest.postForEntity(
                "/api/v1/auth/register", jsonEntity(json), JsonNode.class);
        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        JsonNode body = response.getBody();
        return new AuthData(
                body.get("user").get("id").asText(),
                body.get("accessToken").asText(),
                body.get("refreshToken").asText());
    }

    private HttpEntity<String> jsonEntity(String json) {
        HttpHeaders headers = new HttpHeaders();
        headers.setContentType(MediaType.APPLICATION_JSON);
        return new HttpEntity<>(json, headers);
    }

    private record AuthData(String id, String accessToken, String refreshToken) {
    }

    @Test
    @Order(1)
    void registerLoginAndProfile() {
        String name = "user_" + suffix();
        AuthData auth = register(name, name + "@example.com", "Первый ник");

        ResponseEntity<JsonNode> me = authed(HttpMethod.GET, "/api/v1/users/me", auth.accessToken(), null);
        assertThat(me.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(me.getBody().get("user").get("email").asText()).isEqualTo(name + "@example.com");
        assertThat(me.getBody().get("user").get("nickname").asText()).isEqualTo("Первый ник");

        String json = """
                {"nickname":"Новый ник","bio":"первая строка\\nвторая"}
                """;
        ResponseEntity<JsonNode> patched = authed(HttpMethod.PATCH, "/api/v1/users/me", auth.accessToken(), json);
        assertThat(patched.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(patched.getBody().get("user").get("nickname").asText()).isEqualTo("Новый ник");
        assertThat(patched.getBody().get("user").get("bio").asText()).isEqualTo("первая строка\nвторая");
    }

    @Test
    @Order(2)
    void loginAcceptsUsernameAndEmailCaseInsensitively() {
        String name = "login_" + suffix();
        register(name, name + "@example.com", name);

        JsonNode byUsername = post("/api/v1/auth/login",
                jsonEntity("{\"login\":\"" + name.toUpperCase() + "\",\"password\":\"password123\"}"));
        assertThat(byUsername.get("tokenType").asText()).isEqualTo("Bearer");
        assertThat(byUsername.get("accessToken").asText()).isNotBlank();
        assertThat(byUsername.get("refreshToken").asText()).isNotBlank();

        JsonNode byEmail = post("/api/v1/auth/login",
                jsonEntity("{\"login\":\"" + name + "@example.com\",\"password\":\"password123\"}"));
        assertThat(byEmail.get("user").get("username").asText()).isEqualTo(name);
    }

    @Test
    @Order(3)
    void registrationConflictsAndValidation() {
        String name = "conflict_" + suffix();
        register(name, name + "@example.com", name);

        ResponseEntity<JsonNode> sameUsername = rest.postForEntity("/api/v1/auth/register",
                jsonEntity("""
                        {"email":"another-%s@example.com","username":"%s","password":"password123"}
                        """.formatted(suffix(), name)), JsonNode.class);
        assertThat(sameUsername.getStatusCode()).isEqualTo(HttpStatus.CONFLICT);
        assertThat(sameUsername.getBody().get("error").get("code").asText()).isEqualTo("USERNAME_TAKEN");

        ResponseEntity<JsonNode> sameEmail = rest.postForEntity("/api/v1/auth/register",
                jsonEntity("""
                        {"email":"%s@example.com","username":"other_%s","password":"password123"}
                        """.formatted(name, suffix())), JsonNode.class);
        assertThat(sameEmail.getStatusCode()).isEqualTo(HttpStatus.CONFLICT);
        assertThat(sameEmail.getBody().get("error").get("code").asText()).isEqualTo("EMAIL_TAKEN");

        ResponseEntity<JsonNode> invalid = rest.postForEntity("/api/v1/auth/register",
                jsonEntity("{\"email\":\"bad\",\"username\":\"1x\",\"password\":\"short\"}"), JsonNode.class);
        assertThat(invalid.getStatusCode()).isEqualTo(HttpStatus.UNPROCESSABLE_ENTITY);
        JsonNode details = invalid.getBody().get("error").get("details");
        assertThat(details.has("email")).isTrue();
        assertThat(details.has("username")).isTrue();
        assertThat(details.has("password")).isTrue();
    }

    @Test
    @Order(4)
    void nicknameDoesNotNeedToBeUniqueButUsernameDoes() {
        String nickname = "Общий ник " + suffix();
        String first = "nick_a_" + suffix();
        String second = "nick_b_" + suffix();
        AuthData a = register(first, first + "@example.com", nickname);
        AuthData b = register(second, second + "@example.com", nickname);
        assertThat(b.id()).isNotEqualTo(a.id());

        ResponseEntity<JsonNode> taken = authed(HttpMethod.PATCH, "/api/v1/users/me", b.accessToken(),
                "{\"username\":\"" + first + "\"}");
        assertThat(taken.getStatusCode()).isEqualTo(HttpStatus.CONFLICT);
        assertThat(taken.getBody().get("error").get("code").asText()).isEqualTo("USERNAME_TAKEN");

        String availableName = "free_" + suffix();
        ResponseEntity<JsonNode> renamed = authed(HttpMethod.PATCH, "/api/v1/users/me", b.accessToken(),
                "{\"username\":\"" + availableName + "\"}");
        assertThat(renamed.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(renamed.getBody().get("user").get("username").asText()).isEqualTo(availableName);

        JsonNode availability = rest.getForEntity(
                "/api/v1/users/username-available?username=" + availableName, JsonNode.class).getBody();
        assertThat(availability.get("available").asBoolean()).isFalse();
        assertThat(availability.get("reason").asText()).isEqualTo("taken");

        JsonNode free = rest.getForEntity(
                "/api/v1/users/username-available?username=free_" + suffix(), JsonNode.class).getBody();
        assertThat(free.get("available").asBoolean()).isTrue();

        JsonNode reserved = rest.getForEntity(
                "/api/v1/users/username-available?username=admin", JsonNode.class).getBody();
        assertThat(reserved.get("available").asBoolean()).isFalse();
        assertThat(reserved.get("reason").asText()).isEqualTo("reserved");
    }

    @Test
    @Order(5)
    void publicProfileHidesEmail() {
        String owner = "owner_" + suffix();
        String viewer = "viewer_" + suffix();
        AuthData ownerAuth = register(owner, owner + "@example.com", owner);
        AuthData viewerAuth = register(viewer, viewer + "@example.com", viewer);

        ResponseEntity<JsonNode> response = authed(
                HttpMethod.GET, "/api/v1/users/" + ownerAuth.id(), viewerAuth.accessToken(), null);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody().get("user").has("email")).isFalse();
        assertThat(response.getBody().get("user").get("username").asText()).isEqualTo(owner);
    }

    @Test
    @Order(6)
    void avatarUploadServeAndDelete() {
        String name = "avatar_" + suffix();
        AuthData auth = register(name, name + "@example.com", name);

        MultiValueMap<String, Object> form = new LinkedMultiValueMap<>();
        form.add("avatar", new ByteArrayResource(PNG) {
            @Override
            public String getFilename() {
                return "avatar.png";
            }
        });

        HttpHeaders headers = new HttpHeaders();
        headers.setBearerAuth(auth.accessToken());
        headers.setContentType(MediaType.MULTIPART_FORM_DATA);
        ResponseEntity<JsonNode> uploaded = rest.exchange(
                "/api/v1/users/me/avatar", HttpMethod.POST, new HttpEntity<>(form, headers), JsonNode.class);

        assertThat(uploaded.getStatusCode()).isEqualTo(HttpStatus.OK);
        String avatarUrl = uploaded.getBody().get("user").get("avatarUrl").asText();
        assertThat(avatarUrl).startsWith(PUBLIC_BASE + "/media/avatars/").endsWith(".png");

        String mediaPath = avatarUrl.substring(PUBLIC_BASE.length());
        ResponseEntity<byte[]> media = rest.getForEntity(mediaPath, byte[].class);
        assertThat(media.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(media.getHeaders().getContentType()).isEqualTo(MediaType.IMAGE_PNG);
        assertThat(media.getBody()).isEqualTo(PNG);

        ResponseEntity<JsonNode> deleted = authed(HttpMethod.DELETE, "/api/v1/users/me/avatar", auth.accessToken(), null);
        assertThat(deleted.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(deleted.getBody().get("user").get("avatarUrl").isNull()).isTrue();

        ResponseEntity<byte[]> gone = rest.getForEntity(mediaPath, byte[].class);
        assertThat(gone.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);
    }

    @Test
    @Order(7)
    void nonImageAvatarIsRejected() {
        String name = "badavatar_" + suffix();
        AuthData auth = register(name, name + "@example.com", name);

        MultiValueMap<String, Object> form = new LinkedMultiValueMap<>();
        form.add("avatar", new ByteArrayResource("not an image".getBytes()) {
            @Override
            public String getFilename() {
                return "notes.txt";
            }
        });

        HttpHeaders headers = new HttpHeaders();
        headers.setBearerAuth(auth.accessToken());
        headers.setContentType(MediaType.MULTIPART_FORM_DATA);
        ResponseEntity<JsonNode> response = rest.exchange(
                "/api/v1/users/me/avatar", HttpMethod.POST, new HttpEntity<>(form, headers), JsonNode.class);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.UNSUPPORTED_MEDIA_TYPE);
        assertThat(response.getBody().get("error").get("code").asText()).isEqualTo("UNSUPPORTED_MEDIA_TYPE");
    }

    @Test
    @Order(8)
    void chatsReturnEmptyStub() {
        String name = "chats_" + suffix();
        AuthData auth = register(name, name + "@example.com", name);

        ResponseEntity<JsonNode> response = authed(HttpMethod.GET, "/api/v1/chats", auth.accessToken(), null);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody().get("items").isArray()).isTrue();
        assertThat(response.getBody().get("items").size()).isZero();
        assertThat(response.getBody().get("nextCursor").isNull()).isTrue();
    }

    @Test
    @Order(9)
    void protectedEndpointsRequireValidToken() {
        ResponseEntity<JsonNode> missing = rest.getForEntity("/api/v1/users/me", JsonNode.class);
        assertThat(missing.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(missing.getBody().get("error").get("code").asText()).isEqualTo("UNAUTHORIZED");

        ResponseEntity<JsonNode> bad = authed(HttpMethod.GET, "/api/v1/users/me", "not.a.jwt", null);
        assertThat(bad.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);

        ResponseEntity<JsonNode> wrongPassword = rest.postForEntity("/api/v1/auth/login",
                jsonEntity("{\"login\":\"nobody@example.com\",\"password\":\"password123\"}"), JsonNode.class);
        assertThat(wrongPassword.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(wrongPassword.getBody().get("error").get("code").asText()).isEqualTo("INVALID_CREDENTIALS");
    }

    @Test
    @Order(10)
    void refreshRotatesAndDetectsReuse() {
        String name = "refresh_" + suffix();
        AuthData auth = register(name, name + "@example.com", name);

        ResponseEntity<JsonNode> first = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), JsonNode.class);
        assertThat(first.getStatusCode()).isEqualTo(HttpStatus.OK);
        String rotated = first.getBody().get("refreshToken").asText();
        assertThat(rotated).isNotEqualTo(auth.refreshToken());

        ResponseEntity<JsonNode> reuse = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), JsonNode.class);
        assertThat(reuse.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(reuse.getBody().get("error").get("code").asText()).isEqualTo("INVALID_REFRESH_TOKEN");

        ResponseEntity<JsonNode> afterReuse = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + rotated + "\"}"), JsonNode.class);
        assertThat(afterReuse.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
    }

    @Test
    @Order(11)
    void logoutRevokesRefreshToken() {
        String name = "logout_" + suffix();
        AuthData auth = register(name, name + "@example.com", name);

        ResponseEntity<Void> logout = rest.postForEntity("/api/v1/auth/logout",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), Void.class);
        assertThat(logout.getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);

        ResponseEntity<JsonNode> refresh = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), JsonNode.class);
        assertThat(refresh.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
    }

    @Test
    @Order(13)
    void oversizedAvatarIsRejectedWithCleanResponse() {
        String name = "bigavatar_" + suffix();
        AuthData auth = register(name, name + "@example.com", name);

        byte[] big = new byte[6 * 1024 * 1024];
        MultiValueMap<String, Object> form = new LinkedMultiValueMap<>();
        form.add("avatar", new ByteArrayResource(big) {
            @Override
            public String getFilename() {
                return "big.png";
            }
        });

        HttpHeaders headers = new HttpHeaders();
        headers.setBearerAuth(auth.accessToken());
        headers.setContentType(MediaType.MULTIPART_FORM_DATA);
        ResponseEntity<JsonNode> response = rest.exchange(
                "/api/v1/users/me/avatar", HttpMethod.POST, new HttpEntity<>(form, headers), JsonNode.class);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.PAYLOAD_TOO_LARGE);
        assertThat(response.getBody().get("error").get("code").asText()).isEqualTo("AVATAR_TOO_LARGE");
    }

    @Test
    @Order(12)
    void corsPreflightAndUnknownRoutes() {
        HttpHeaders headers = new HttpHeaders();
        headers.setOrigin("http://example.com");
        headers.setAccessControlRequestMethod(HttpMethod.GET);
        ResponseEntity<Void> preflight = rest.exchange(
                "/api/v1/users/me", HttpMethod.OPTIONS, new HttpEntity<>(headers), Void.class);
        assertThat(preflight.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(preflight.getHeaders().getAccessControlAllowOrigin()).isEqualTo("http://example.com");

        String name = "routes_" + suffix();
        AuthData auth = register(name, name + "@example.com", name);
        ResponseEntity<JsonNode> unknown = authed(HttpMethod.GET, "/api/v1/unknown-route", auth.accessToken(), null);
        assertThat(unknown.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);
        assertThat(unknown.getBody().get("error").get("code").asText()).isEqualTo("NOT_FOUND");
    }
}
