package com.goping;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.goping.config.DeletePolicy;
import com.goping.github.GitHubClient;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.mock.mockito.MockBean;
import org.springframework.boot.test.web.client.TestRestTemplate;
import org.springframework.boot.test.web.server.LocalServerPort;
import org.springframework.core.io.ByteArrayResource;
import org.springframework.http.HttpEntity;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpMethod;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.http.client.JdkClientHttpRequestFactory;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;
import org.springframework.util.LinkedMultiValueMap;
import org.springframework.util.MultiValueMap;
import org.springframework.web.socket.CloseStatus;
import org.springframework.web.socket.WebSocketHandler;
import org.springframework.web.socket.WebSocketHttpHeaders;
import org.springframework.web.socket.WebSocketMessage;
import org.springframework.web.socket.WebSocketSession;
import org.springframework.web.socket.client.standard.StandardWebSocketClient;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.net.URI;
import java.nio.file.Path;
import java.util.Base64;
import java.util.UUID;
import java.util.concurrent.TimeUnit;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.when;

/**
 * Сквозные тесты GoPing API: настоящий HTTP, настоящая SQLite во временной папке.
 */
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
class ApiIntegrationTest {

    private static final String PUBLIC_BASE = "http://cdn.example.test";
    private static final String ADMIN_SECRET = "integration-admin-secret";
    private static final Path ROOT = createRoot();

    private static final byte[] PNG = Base64.getDecoder().decode(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    @Autowired
    private TestRestTemplate rest;

    @LocalServerPort
    private int port;

    @MockBean
    private GitHubClient githubClient;

    @Autowired
    private JdbcTemplate jdbc;

    @Autowired
    private DeletePolicy deletePolicy;

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
        registry.add("app.admin-secret", () -> ADMIN_SECRET);
        registry.add("app.github-client-id", () -> "test-github-client");
        registry.add("app.github-client-secret", () -> "test-github-secret");
        registry.add("app.github-callback-url", () -> "http://localhost/api/v1/auth/github/callback");
    }

    private static Path createRoot() {
        try {
            return Files.createTempDirectory("goping-it");
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    private String suffix() {
        return UUID.randomUUID().toString().substring(0, 8);
    }

    private HttpEntity<String> jsonEntity(String json) {
        HttpHeaders headers = new HttpHeaders();
        headers.setContentType(MediaType.APPLICATION_JSON);
        return new HttpEntity<>(json, headers);
    }

    private ResponseEntity<JsonNode> authed(HttpMethod method, String path, String token, Object body) {
        HttpHeaders headers = new HttpHeaders();
        headers.setBearerAuth(token);
        if (body != null) {
            headers.setContentType(MediaType.APPLICATION_JSON);
        }
        return rest.exchange(path, method, new HttpEntity<>(body, headers), JsonNode.class);
    }

    private record AuthData(
            String id,
            String username,
            String nickname,
            String avatarUrl,
            String accessToken,
            String refreshToken
    ) {
    }

    private AuthData auth(JsonNode body) {
        JsonNode user = body.get("user");
        return new AuthData(
                user.get("id").asText(),
                user.get("username").asText(),
                user.get("nickname").asText(),
                user.hasNonNull("avatarUrl") ? user.get("avatarUrl").asText() : null,
                body.get("accessToken").asText(),
                body.get("refreshToken").asText()
        );
    }

    private AuthData tokenLogin(String token) {
        ResponseEntity<JsonNode> response = rest.postForEntity("/api/v1/auth/token",
                jsonEntity("{\"token\":\"" + token + "\"}"), JsonNode.class);
        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        return auth(response.getBody());
    }

    private JsonNode issueTokenNode(String label) {
        HttpHeaders headers = new HttpHeaders();
        headers.set("X-Admin-Secret", ADMIN_SECRET);
        headers.setContentType(MediaType.APPLICATION_JSON);
        ResponseEntity<JsonNode> response = rest.exchange(
                "/api/v1/admin/tokens", HttpMethod.POST,
                new HttpEntity<>("{\"label\":\"" + label + "\"}", headers), JsonNode.class);
        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        return response.getBody();
    }

    private String issueToken() {
        return issueTokenNode("test").get("value").asText();
    }

    private JsonNode adminList() {
        HttpHeaders headers = new HttpHeaders();
        headers.set("X-Admin-Secret", ADMIN_SECRET);
        ResponseEntity<JsonNode> response = rest.exchange(
                "/api/v1/admin/tokens", HttpMethod.GET, new HttpEntity<>(headers), JsonNode.class);
        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        return response.getBody();
    }

    private JsonNode githubStart() {
        ResponseEntity<JsonNode> response = rest.getForEntity(
                "/api/v1/auth/github/start", JsonNode.class);
        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        return response.getBody();
    }

    private AuthData githubLogin(String code) {
        JsonNode start = githubStart();
        String state = start.get("state").asText();
        assertThat(start.get("authorizeUrl").asText())
                .startsWith("https://github.com/login/oauth/authorize")
                .contains("state=" + state);

        ResponseEntity<String> callback = rest.getForEntity(
                "/api/v1/auth/github/callback?code=" + code + "&state=" + state, String.class);
        assertThat(callback.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(callback.getBody()).contains("GoPing");

        ResponseEntity<JsonNode> poll = rest.getForEntity(
                "/api/v1/auth/github/poll?state=" + state, JsonNode.class);
        assertThat(poll.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(poll.getBody().get("status").asText()).isEqualTo("ready");
        return auth(poll.getBody().get("auth"));
    }

    @Test
    void serverTokenActivatesOnceAndAlwaysLogsIntoTheSameUser() {
        String raw = issueToken();
        assertThat(raw).matches("GOPING-[A-Za-z0-9_-]{12}");

        AuthData first = tokenLogin(raw);
        assertThat(first.username()).startsWith("ping_");
        assertThat(first.nickname()).startsWith("Ping ");

        AuthData second = tokenLogin(raw);
        assertThat(second.id()).isEqualTo(first.id());
        assertThat(second.username()).isEqualTo(first.username());

        boolean activated = false;
        for (JsonNode item : adminList().get("items")) {
            if (first.id().equals(item.path("userId").asText())) {
                activated = true;
                assertThat(item.path("activatedAt").isNull()).isFalse();
            }
        }
        assertThat(activated).isTrue();
    }

    @Test
    void adminSecretIsRequiredAndRevokedTokenCannotLogin() {
        ResponseEntity<JsonNode> noSecret = rest.postForEntity(
                "/api/v1/admin/tokens", jsonEntity("{}"), JsonNode.class);
        assertThat(noSecret.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(noSecret.getBody().get("error").get("code").asText())
                .isEqualTo("ADMIN_UNAUTHORIZED");

        HttpHeaders wrong = new HttpHeaders();
        wrong.set("X-Admin-Secret", "wrong");
        wrong.setContentType(MediaType.APPLICATION_JSON);
        ResponseEntity<JsonNode> wrongSecret = rest.exchange(
                "/api/v1/admin/tokens", HttpMethod.POST,
                new HttpEntity<>("{}", wrong), JsonNode.class);
        assertThat(wrongSecret.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);

        JsonNode issued = issueTokenNode("revoked");
        String raw = issued.get("value").asText();
        String id = issued.get("token").get("id").asText();

        HttpHeaders admin = new HttpHeaders();
        admin.set("X-Admin-Secret", ADMIN_SECRET);
        ResponseEntity<Void> revoked = rest.exchange(
                "/api/v1/admin/tokens/" + id, HttpMethod.DELETE,
                new HttpEntity<>(admin), Void.class);
        assertThat(revoked.getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);

        ResponseEntity<JsonNode> login = rest.postForEntity(
                "/api/v1/auth/token", jsonEntity("{\"token\":\"" + raw + "\"}"), JsonNode.class);
        assertThat(login.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(login.getBody().get("error").get("code").asText())
                .isEqualTo("INVALID_SERVER_TOKEN");
    }

    @Test
    void unknownAndMalformedServerTokensAreRejected() {
        ResponseEntity<JsonNode> unknown = rest.postForEntity("/api/v1/auth/token",
                jsonEntity("{\"token\":\"GOPING-abcdefghijkl\"}"), JsonNode.class);
        assertThat(unknown.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(unknown.getBody().get("error").get("code").asText())
                .isEqualTo("INVALID_SERVER_TOKEN");

        ResponseEntity<JsonNode> malformed = rest.postForEntity("/api/v1/auth/token",
                jsonEntity("{\"token\":\"not-a-token\"}"), JsonNode.class);
        assertThat(malformed.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
    }

    @Test
    void tokenUserCanChangeAutofilledProfile() {
        AuthData auth = tokenLogin(issueToken());
        String newUsername = "edited_" + suffix();

        ResponseEntity<JsonNode> patched = authed(HttpMethod.PATCH, "/api/v1/users/me", auth.accessToken(),
                "{\"nickname\":\"Мой ник\",\"username\":\"" + newUsername + "\",\"bio\":\"локальный профиль\"}");
        assertThat(patched.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(patched.getBody().get("user").get("nickname").asText()).isEqualTo("Мой ник");
        assertThat(patched.getBody().get("user").get("username").asText()).isEqualTo(newUsername);

        ResponseEntity<JsonNode> me = authed(HttpMethod.GET, "/api/v1/users/me", auth.accessToken(), null);
        assertThat(me.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(me.getBody().get("user").get("bio").asText()).isEqualTo("локальный профиль");
        assertThat(me.getBody().get("user").has("email")).isFalse();

        JsonNode availability = rest.getForEntity(
                "/api/v1/users/username-available?username=" + newUsername, JsonNode.class).getBody();
        assertThat(availability.get("available").asBoolean()).isFalse();
        assertThat(availability.get("reason").asText()).isEqualTo("taken");
    }

    @Test
    void usernameConflictBetweenTwoTokenUsers() {
        AuthData first = tokenLogin(issueToken());
        AuthData second = tokenLogin(issueToken());

        ResponseEntity<JsonNode> conflict = authed(HttpMethod.PATCH, "/api/v1/users/me", second.accessToken(),
                "{\"username\":\"" + first.username() + "\"}");
        assertThat(conflict.getStatusCode()).isEqualTo(HttpStatus.CONFLICT);
        assertThat(conflict.getBody().get("error").get("code").asText())
                .isEqualTo("USERNAME_TAKEN");
    }

    @Test
    void githubLoginFillsAvatarAndNicknameAndKeepsLaterEdits() {
        String login = "octo_" + suffix();
        when(githubClient.authenticate("good-code")).thenReturn(new GitHubClient.Profile(
                900000L + Math.abs(login.hashCode()),
                login,
                "Octo Cat",
                "https://avatars.example/octo.png"
        ));

        AuthData first = githubLogin("good-code");
        assertThat(first.username()).isEqualTo(login);
        assertThat(first.nickname()).isEqualTo("Octo Cat");
        assertThat(first.avatarUrl()).isEqualTo("https://avatars.example/octo.png");

        ResponseEntity<JsonNode> patched = authed(HttpMethod.PATCH, "/api/v1/users/me", first.accessToken(),
                "{\"nickname\":\"Мой GitHub\"}");
        assertThat(patched.getStatusCode()).isEqualTo(HttpStatus.OK);

        AuthData again = githubLogin("good-code");
        assertThat(again.id()).isEqualTo(first.id());
        assertThat(again.nickname()).isEqualTo("Мой GitHub");
        assertThat(again.avatarUrl()).isEqualTo("https://avatars.example/octo.png");
    }

    @Test
    void githubLoginWithoutProfileIsPendingAndUnknownStateExpires() {
        JsonNode start = githubStart();
        String state = start.get("state").asText();

        ResponseEntity<JsonNode> pending = rest.getForEntity(
                "/api/v1/auth/github/poll?state=" + state, JsonNode.class);
        assertThat(pending.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(pending.getBody().get("status").asText()).isEqualTo("pending");

        ResponseEntity<JsonNode> missing = rest.getForEntity(
                "/api/v1/auth/github/poll?state=unknown-state", JsonNode.class);
        assertThat(missing.getStatusCode()).isEqualTo(HttpStatus.GONE);
        assertThat(missing.getBody().get("error").get("code").asText())
                .isEqualTo("GITHUB_STATE_EXPIRED");
    }

    @Test
    void githubCallbackWithoutStateOrWithCancellationShowsFailurePage() {
        ResponseEntity<String> noState = rest.getForEntity(
                "/api/v1/auth/github/callback", String.class);
        assertThat(noState.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(noState.getBody()).contains("Не удалось войти");

        JsonNode start = githubStart();
        String state = start.get("state").asText();
        ResponseEntity<String> cancelled = rest.getForEntity(
                "/api/v1/auth/github/callback?error=access_denied&state=" + state, String.class);
        assertThat(cancelled.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(cancelled.getBody()).contains("Не удалось войти");

        ResponseEntity<JsonNode> afterCancel = rest.getForEntity(
                "/api/v1/auth/github/poll?state=" + state, JsonNode.class);
        assertThat(afterCancel.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(afterCancel.getBody().get("error").get("code").asText())
                .isEqualTo("GITHUB_AUTH_FAILED");
    }

    @Test
    void protectedEndpointsRequireValidToken() {
        ResponseEntity<JsonNode> missing = rest.getForEntity("/api/v1/users/me", JsonNode.class);
        assertThat(missing.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(missing.getBody().get("error").get("code").asText()).isEqualTo("UNAUTHORIZED");

        ResponseEntity<JsonNode> bad = authed(HttpMethod.GET, "/api/v1/users/me", "not.a.jwt", null);
        assertThat(bad.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
    }

    @Test
    void refreshRotatesAndDetectsReuse() {
        AuthData auth = tokenLogin(issueToken());

        ResponseEntity<JsonNode> first = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), JsonNode.class);
        assertThat(first.getStatusCode()).isEqualTo(HttpStatus.OK);
        String rotated = first.getBody().get("refreshToken").asText();
        assertThat(rotated).isNotEqualTo(auth.refreshToken());

        ResponseEntity<JsonNode> reuse = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), JsonNode.class);
        assertThat(reuse.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(reuse.getBody().get("error").get("code").asText())
                .isEqualTo("INVALID_REFRESH_TOKEN");

        ResponseEntity<JsonNode> afterReuse = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + rotated + "\"}"), JsonNode.class);
        assertThat(afterReuse.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
    }

    @Test
    void logoutRevokesRefreshToken() {
        AuthData auth = tokenLogin(issueToken());

        ResponseEntity<Void> logout = rest.postForEntity("/api/v1/auth/logout",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), Void.class);
        assertThat(logout.getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);

        ResponseEntity<JsonNode> refresh = rest.postForEntity("/api/v1/auth/refresh",
                jsonEntity("{\"refreshToken\":\"" + auth.refreshToken() + "\"}"), JsonNode.class);
        assertThat(refresh.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
    }

    @Test
    void avatarUploadServeAndDeleteReplacesGithubAvatar() {
        AuthData auth = tokenLogin(issueToken());

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

        ResponseEntity<JsonNode> deleted = authed(HttpMethod.DELETE, "/api/v1/users/me/avatar",
                auth.accessToken(), null);
        assertThat(deleted.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(deleted.getBody().get("user").get("avatarUrl").isNull()).isTrue();

        ResponseEntity<byte[]> gone = rest.getForEntity(mediaPath, byte[].class);
        assertThat(gone.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);
    }

    @Test
    void nonImageAvatarIsRejected() {
        AuthData auth = tokenLogin(issueToken());

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
        assertThat(response.getBody().get("error").get("code").asText())
                .isEqualTo("UNSUPPORTED_MEDIA_TYPE");
    }

    @Test
    void oversizedAvatarIsRejectedWithCleanResponse() {
        AuthData auth = tokenLogin(issueToken());

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
        assertThat(response.getBody().get("error").get("code").asText())
                .isEqualTo("AVATAR_TOO_LARGE");
    }

    @Test
    void chatsReturnFavoritesChat() {
        AuthData auth = tokenLogin(issueToken());

        ResponseEntity<JsonNode> response = authed(HttpMethod.GET, "/api/v1/chats", auth.accessToken(), null);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody().get("items").isArray()).isTrue();
        assertThat(response.getBody().get("items").size()).isEqualTo(1);
        JsonNode favorites = chatOfKind(response.getBody().get("items"), "favorites");
        assertThat(favorites.get("title").asText()).isEqualTo("Favorites");
        assertThat(favorites.get("peerUserId").isNull()).isTrue();
        assertThat(response.getBody().get("nextCursor").isNull()).isTrue();
    }

    @Test
    void favoritesChatIsCreatedOnceAndWritableOnlyByOwner() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());

        JsonNode first = authed(HttpMethod.GET, "/api/v1/chats", alice.accessToken(), null)
                .getBody().get("items");
        String favoritesId = chatOfKind(first, "favorites").get("id").asText();

        JsonNode second = authed(HttpMethod.GET, "/api/v1/chats", alice.accessToken(), null)
                .getBody().get("items");
        assertThat(second.size()).isEqualTo(1);
        assertThat(chatOfKind(second, "favorites").get("id").asText()).isEqualTo(favoritesId);

        ResponseEntity<JsonNode> sent = authed(HttpMethod.POST,
                "/api/v1/chats/" + favoritesId + "/messages", alice.accessToken(),
                "{\"text\":\"Моя заметка\"}");
        assertThat(sent.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        assertThat(sent.getBody().get("text").asText()).isEqualTo("Моя заметка");

        JsonNode messages = authed(HttpMethod.GET,
                "/api/v1/chats/" + favoritesId + "/messages", alice.accessToken(), null)
                .getBody().get("items");
        assertThat(messages.size()).isEqualTo(1);
        assertThat(messages.get(0).get("text").asText()).isEqualTo("Моя заметка");

        authed(HttpMethod.GET, "/api/v1/chats", bob.accessToken(), null);
        ResponseEntity<JsonNode> outsider = authed(HttpMethod.GET,
                "/api/v1/chats/" + favoritesId + "/messages", bob.accessToken(), null);
        assertThat(outsider.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);
    }

    @Test
    void corsPreflightAndUnknownRoutes() {
        HttpHeaders headers = new HttpHeaders();
        headers.setOrigin("http://example.com");
        headers.setAccessControlRequestMethod(HttpMethod.GET);
        ResponseEntity<Void> preflight = rest.exchange(
                "/api/v1/users/me", HttpMethod.OPTIONS, new HttpEntity<>(headers), Void.class);
        assertThat(preflight.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(preflight.getHeaders().getAccessControlAllowOrigin()).isEqualTo("http://example.com");

        AuthData auth = tokenLogin(issueToken());
        ResponseEntity<JsonNode> unknown = authed(HttpMethod.GET, "/api/v1/unknown-route",
                auth.accessToken(), null);
        assertThat(unknown.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);
        assertThat(unknown.getBody().get("error").get("code").asText()).isEqualTo("NOT_FOUND");
    }

    // ---------- сообщения и ГОСТ-шифрование ----------

    private String createDirectChat(String token, String username) {
        ResponseEntity<JsonNode> response = authed(HttpMethod.POST, "/api/v1/chats", token,
                "{\"username\":\"" + username + "\"}");
        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        assertThat(response.getBody().get("kind").asText()).isEqualTo("direct");
        return response.getBody().get("id").asText();
    }

    private JsonNode chatOfKind(JsonNode items, String kind) {
        for (JsonNode item : items) {
            if (kind.equals(item.path("kind").asText())) {
                return item;
            }
        }
        throw new AssertionError("Chat not found: " + kind);
    }

    @Test
    void messagesAreEncryptedWithGostAndVisibleToBothMembers() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());

        String chatId = createDirectChat(alice.accessToken(), bob.username());
        JsonNode chats = authed(HttpMethod.GET, "/api/v1/chats", alice.accessToken(), null).getBody()
                .get("items");
        JsonNode chat = chatOfKind(chats, "direct");
        assertThat(chat.get("id").asText()).isEqualTo(chatId);
        assertThat(chat.get("title").asText()).isEqualTo(bob.nickname());

        String plain = "Привет, Боб! ГОСТ Р 34.12-2015 «Кузнечик»";
        ResponseEntity<JsonNode> sent = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(),
                "{\"text\":\"" + plain + "\"}");

        assertThat(sent.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        assertThat(sent.getBody().get("text").asText()).isEqualTo(plain);
        assertThat(sent.getBody().get("algorithm").asText())
                .isEqualTo("GOST34.12-2015/KUZNYECHIK/CTR");
        assertThat(sent.getBody().get("sender").get("id").asText()).isEqualTo(alice.id());

        var stored = jdbc.queryForMap(
                "SELECT ciphertext, nonce, mac FROM messages WHERE chat_id = ?", chatId);
        byte[] ciphertext = Base64.getDecoder().decode((String) stored.get("ciphertext"));
        assertThat(ciphertext).isNotEqualTo(plain.getBytes(java.nio.charset.StandardCharsets.UTF_8));
        assertThat(new String(ciphertext, java.nio.charset.StandardCharsets.UTF_8))
                .doesNotContain("Привет");
        assertThat((String) stored.get("nonce")).isNotBlank();
        assertThat((String) stored.get("mac")).isNotBlank();

        ResponseEntity<JsonNode> bobList = authed(HttpMethod.GET,
                "/api/v1/chats/" + chatId + "/messages", bob.accessToken(), null);
        assertThat(bobList.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(bobList.getBody().get("items").size()).isEqualTo(1);
        assertThat(bobList.getBody().get("items").get(0).get("text").asText()).isEqualTo(plain);

        ResponseEntity<JsonNode> reply = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", bob.accessToken(),
                "{\"text\":\"Привет, Алиса!\"}");
        assertThat(reply.getStatusCode()).isEqualTo(HttpStatus.CREATED);

        JsonNode aliceList = authed(HttpMethod.GET,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(), null).getBody();
        assertThat(aliceList.get("items").size()).isEqualTo(2);
        assertThat(aliceList.get("items").get(0).get("text").asText()).isEqualTo(plain);
        assertThat(aliceList.get("items").get(1).get("sender").get("id").asText()).isEqualTo(bob.id());
    }

    @Test
    void tamperedMessageIsReportedAsDamaged() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());

        authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/messages", alice.accessToken(),
                "{\"text\":\"секрет\"}");

        String ciphertext = jdbc.queryForObject(
                "SELECT ciphertext FROM messages WHERE chat_id = ?", String.class, chatId);
        byte[] raw = Base64.getDecoder().decode(ciphertext);
        jdbc.update("UPDATE messages SET ciphertext = ? WHERE chat_id = ?",
                Base64.getEncoder().encodeToString(new byte[raw.length]), chatId);

        JsonNode items = authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                bob.accessToken(), null).getBody().get("items");
        assertThat(items.size()).isEqualTo(1);
        assertThat(items.get(0).get("text").asText()).startsWith("[сообщение повреждено");
    }

    @Test
    void outsiderCannotReadOrWriteChat() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        AuthData eve = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());

        ResponseEntity<JsonNode> read = authed(HttpMethod.GET,
                "/api/v1/chats/" + chatId + "/messages", eve.accessToken(), null);
        assertThat(read.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);

        ResponseEntity<JsonNode> write = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", eve.accessToken(),
                "{\"text\":\"чужой\"}");
        assertThat(write.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);
    }

    @Test
    void creatingSameDirectChatIsIdempotent() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());

        String first = createDirectChat(alice.accessToken(), bob.username());
        String second = createDirectChat(alice.accessToken(), bob.username());
        String fromBob = createDirectChat(bob.accessToken(), alice.username());

        assertThat(second).isEqualTo(first);
        assertThat(fromBob).isEqualTo(first);
        JsonNode chats = authed(HttpMethod.GET, "/api/v1/chats", alice.accessToken(), null)
                .getBody().get("items");
        assertThat(chats.size()).isEqualTo(2);
        assertThat(chatOfKind(chats, "direct").get("id").asText()).isEqualTo(first);
    }

    @Test
    void chatCarriesPeerUsernameAndAvatarUrl() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());

        String chatId = createDirectChat(alice.accessToken(), bob.username());
        JsonNode chats = authed(HttpMethod.GET, "/api/v1/chats", alice.accessToken(), null).getBody()
                .get("items");
        JsonNode chat = chatOfKind(chats, "direct");
        assertThat(chat.get("id").asText()).isEqualTo(chatId);
        assertThat(chat.get("username").asText()).isEqualTo(bob.username());

        ResponseEntity<JsonNode> profile = authed(
                HttpMethod.GET, "/api/v1/users/username/" + bob.username(), alice.accessToken(), null);
        assertThat(profile.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(profile.getBody().get("user").get("id").asText()).isEqualTo(bob.id());
        assertThat(profile.getBody().get("user").get("username").asText()).isEqualTo(bob.username());
        assertThat(profile.getBody().get("user").get("nickname").asText()).isEqualTo(bob.nickname());
    }

    @Test
    void chatListReportsUnreadCount() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());

        authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/messages", bob.accessToken(),
                "{\"text\":\"unread one\"}");
        authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/messages", bob.accessToken(),
                "{\"text\":\"unread two\"}");
        authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/messages", alice.accessToken(),
                "{\"text\":\"my own message\"}");

        JsonNode chats = authed(HttpMethod.GET, "/api/v1/chats", alice.accessToken(), null)
                .getBody().get("items");
        JsonNode direct = chatOfKind(chats, "direct");
        assertThat(direct.get("unreadCount").asInt()).isEqualTo(2);

        authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/read", alice.accessToken(), null);
        chats = authed(HttpMethod.GET, "/api/v1/chats", alice.accessToken(), null)
                .getBody().get("items");
        assertThat(chatOfKind(chats, "direct").get("unreadCount").asInt()).isZero();
    }

    @Test
    void chatAndMessageValidation() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());

        ResponseEntity<JsonNode> self = authed(HttpMethod.POST, "/api/v1/chats",
                alice.accessToken(), "{\"username\":\"" + alice.username() + "\"}");
        assertThat(self.getStatusCode()).isEqualTo(HttpStatus.UNPROCESSABLE_ENTITY);

        ResponseEntity<JsonNode> missing = authed(HttpMethod.POST, "/api/v1/chats",
                alice.accessToken(), "{\"username\":\"nobody_here_123\"}");
        assertThat(missing.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);

        String chatId = createDirectChat(alice.accessToken(), bob.username());

        ResponseEntity<JsonNode> empty = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(), "{\"text\":\"   \"}");
        assertThat(empty.getStatusCode()).isEqualTo(HttpStatus.UNPROCESSABLE_ENTITY);

        String tooLong = "x".repeat(4001);
        ResponseEntity<JsonNode> oversized = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(),
                "{\"text\":\"" + tooLong + "\"}");
        assertThat(oversized.getStatusCode()).isEqualTo(HttpStatus.UNPROCESSABLE_ENTITY);

        ResponseEntity<JsonNode> badId = authed(HttpMethod.GET,
                "/api/v1/chats/not-a-uuid/messages", alice.accessToken(), null);
        assertThat(badId.getStatusCode()).isEqualTo(HttpStatus.BAD_REQUEST);
        assertThat(badId.getBody().get("error").get("code").asText()).isEqualTo("INVALID_ID");
    }

    @Test
    void replyAndDeleteForEveryoneWorkForBothMembers() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());

        JsonNode original = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(),
                "{\"text\":\"original\"}").getBody();
        String originalId = original.get("id").asText();

        ResponseEntity<JsonNode> replyResponse = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", bob.accessToken(),
                "{\"text\":\"reply\",\"replyToId\":\"" + originalId + "\"}");
        assertThat(replyResponse.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        JsonNode reply = replyResponse.getBody();
        assertThat(reply.get("replyTo").get("id").asText()).isEqualTo(originalId);
        assertThat(reply.get("replyTo").get("text").asText()).isEqualTo("original");
        assertThat(reply.get("replyTo").get("senderNickname").asText()).isEqualTo(alice.nickname());

        JsonNode aliceItems = authed(HttpMethod.GET,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(), null)
                .getBody().get("items");
        assertThat(aliceItems.size()).isEqualTo(2);
        assertThat(aliceItems.get(1).get("replyTo").get("id").asText()).isEqualTo(originalId);

        ResponseEntity<JsonNode> deleted = authed(HttpMethod.DELETE,
                "/api/v1/chats/" + chatId + "/messages/" + originalId,
                bob.accessToken(), "{\"forEveryone\":true}");
        assertThat(deleted.getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);

        JsonNode after = authed(HttpMethod.GET,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(), null)
                .getBody().get("items");
        assertThat(after.size()).isEqualTo(1);
        assertThat(after.get(0).get("id").asText()).isEqualTo(reply.get("id").asText());
        assertThat(after.get(0).get("replyTo").isNull()).isTrue();
    }

    @Test
    void deleteForMeHidesMessageOnlyForOneMember() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());

        JsonNode sent = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/messages", alice.accessToken(),
                "{\"text\":\"private deletion\"}").getBody();
        String messageId = sent.get("id").asText();

        ResponseEntity<JsonNode> deleted = authed(HttpMethod.DELETE,
                "/api/v1/chats/" + chatId + "/messages/" + messageId,
                bob.accessToken(), "{\"forEveryone\":false}");
        assertThat(deleted.getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);

        assertThat(authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                bob.accessToken(), null).getBody().get("items").size()).isZero();
        assertThat(authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), null).getBody().get("items").size()).isEqualTo(1);
    }

    @Test
    void readReceiptChangesStatusOnlyForTheSender() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());

        authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), "{\"text\":\"read me\"}");

        JsonNode before = authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), null).getBody().get("items");
        assertThat(before.get(0).get("status").asText()).isEqualTo("sent");

        ResponseEntity<JsonNode> read = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/read", bob.accessToken(), null);
        assertThat(read.getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);

        JsonNode after = authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), null).getBody().get("items");
        assertThat(after.get(0).get("status").asText()).isEqualTo("read");
    }

    @Test
    void websocketHandshakeRequiresValidAccessToken() throws Exception {
        StandardWebSocketClient client = new StandardWebSocketClient();
        WebSocketHandler noop = new WebSocketHandler() {
            @Override
            public void afterConnectionEstablished(WebSocketSession session) {
            }

            @Override
            public void handleMessage(WebSocketSession session, WebSocketMessage<?> message) {
            }

            @Override
            public void handleTransportError(WebSocketSession session, Throwable exception) {
            }

            @Override
            public void afterConnectionClosed(WebSocketSession session, CloseStatus closeStatus) {
            }

            @Override
            public boolean supportsPartialMessages() {
                return false;
            }
        };

        boolean anonymousRejected = false;
        try {
            client.doHandshake(noop, (WebSocketHttpHeaders) null,
                            new URI("ws://localhost:" + port + "/api/v1/ws"))
                    .get(5, TimeUnit.SECONDS);
        } catch (Exception e) {
            anonymousRejected = true;
        }
        assertThat(anonymousRejected).isTrue();

        AuthData auth = tokenLogin(issueToken());
        URI uri = new URI("ws://localhost:" + port + "/api/v1/ws?token=" + auth.accessToken());
        try (WebSocketSession session = client.doHandshake(noop, (WebSocketHttpHeaders) null, uri)
                .get(5, TimeUnit.SECONDS)) {
            assertThat(session.isOpen()).isTrue();
        }
    }

    @Test
    void clientConfigExposesDeleteOthersFlag() {
        AuthData auth = tokenLogin(issueToken());
        JsonNode config = authed(HttpMethod.GET, "/api/v1/client-config",
                auth.accessToken(), null).getBody();
        assertThat(config.get("allowDeleteOthersMessages").asBoolean()).isTrue();

        deletePolicy.setAllowDeleteOthers(false);
        try {
            config = authed(HttpMethod.GET, "/api/v1/client-config",
                    auth.accessToken(), null).getBody();
            assertThat(config.get("allowDeleteOthersMessages").asBoolean()).isFalse();
        } finally {
            deletePolicy.setAllowDeleteOthers(true);
        }
    }

    @Test
    void deletingOthersMessagesCanBeDisabledOnServer() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());
        String messageId = authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), "{\"text\":\"do not delete me\"}").getBody().get("id").asText();

        deletePolicy.setAllowDeleteOthers(false);
        try {
            ResponseEntity<JsonNode> hide = authed(HttpMethod.DELETE,
                    "/api/v1/chats/" + chatId + "/messages/" + messageId,
                    bob.accessToken(), "{\"forEveryone\":false}");
            assertThat(hide.getStatusCode()).isEqualTo(HttpStatus.FORBIDDEN);
            assertThat(hide.getBody().get("error").get("code").asText())
                    .isEqualTo("DELETE_OTHERS_DISABLED");

            ResponseEntity<JsonNode> purge = authed(HttpMethod.DELETE,
                    "/api/v1/chats/" + chatId + "/messages/" + messageId,
                    bob.accessToken(), "{\"forEveryone\":true}");
            assertThat(purge.getStatusCode()).isEqualTo(HttpStatus.FORBIDDEN);
        } finally {
            deletePolicy.setAllowDeleteOthers(true);
        }

        JsonNode items = authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), null).getBody().get("items");
        assertThat(items.size()).isEqualTo(1);
        assertThat(items.get(0).get("id").asText()).isEqualTo(messageId);
    }

    @Test
    void messagePinsSupportPrivateAndChatScopes() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());
        String messageId = authed(HttpMethod.POST, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), "{\"text\":\"pin me\"}").getBody().get("id").asText();
        String pinPath = "/api/v1/chats/" + chatId + "/messages/" + messageId + "/pin";

        assertThat(authed(HttpMethod.POST, pinPath, alice.accessToken(),
                "{\"scope\":\"me\"}").getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);
        assertThat(pinScopeOf(alice, chatId)).isEqualTo("me");
        assertThat(pinScopeOf(bob, chatId)).isNull();

        assertThat(authed(HttpMethod.POST, pinPath, alice.accessToken(),
                "{\"scope\":\"chat\"}").getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);
        assertThat(pinScopeOf(alice, chatId)).isEqualTo("chat");
        assertThat(pinScopeOf(bob, chatId)).isEqualTo("chat");

        // Общий пин может снять любой участник чата.
        assertThat(authed(HttpMethod.DELETE, pinPath, bob.accessToken(),
                "{\"scope\":\"chat\"}").getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);
        // У Алисы остаётся её личный пин, у Боба пинов больше нет.
        assertThat(pinScopeOf(alice, chatId)).isEqualTo("me");
        assertThat(pinScopeOf(bob, chatId)).isNull();

        assertThat(authed(HttpMethod.DELETE, pinPath, alice.accessToken(),
                "{\"scope\":\"me\"}").getStatusCode()).isEqualTo(HttpStatus.NO_CONTENT);
        assertThat(pinScopeOf(alice, chatId)).isNull();
        assertThat(pinScopeOf(bob, chatId)).isNull();

        ResponseEntity<JsonNode> invalid = authed(HttpMethod.POST, pinPath, alice.accessToken(),
                "{\"scope\":\"everyone\"}");
        assertThat(invalid.getStatusCode()).isEqualTo(HttpStatus.BAD_REQUEST);
        assertThat(invalid.getBody().get("error").get("code").asText())
                .isEqualTo("INVALID_PIN_SCOPE");
    }

    private String pinScopeOf(AuthData auth, String chatId) {
        JsonNode items = authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                auth.accessToken(), null).getBody().get("items");
        JsonNode scope = items.get(0).get("pinScope");
        return scope == null || scope.isNull() ? null : scope.asText();
    }

    // ---------- 1.0.2 additions ----------

    private String favoritesChatId(AuthData auth) {
        JsonNode items = authed(HttpMethod.GET, "/api/v1/chats", auth.accessToken(), null)
                .getBody().get("items");
        return chatOfKind(items, "favorites").get("id").asText();
    }

    private ResponseEntity<JsonNode> adminPost(String path, String json) {
        HttpHeaders headers = new HttpHeaders();
        headers.set("X-Admin-Secret", ADMIN_SECRET);
        headers.setContentType(MediaType.APPLICATION_JSON);
        return rest.exchange(path, HttpMethod.POST, new HttpEntity<>(json, headers), JsonNode.class);
    }

    @Test
    void clientConfigExposesLiveLimitsAndAdminCanChangeThem() {
        AuthData auth = tokenLogin(issueToken());
        JsonNode config = authed(HttpMethod.GET, "/api/v1/client-config",
                auth.accessToken(), null).getBody();
        JsonNode limits = config.get("limits");
        assertThat(limits.get("maxTextLength").asInt()).isEqualTo(4000);
        assertThat(limits.get("maxVoiceSeconds").asInt()).isEqualTo(300);
        assertThat(limits.get("maxCallMinutes").asInt()).isZero();
        assertThat(limits.get("maxFileBytes").asLong()).isEqualTo(52428800L);

        try {
            ResponseEntity<JsonNode> changed = adminPost("/api/v1/admin/limits",
                    "{\"maxTextLength\":10,\"maxCallMinutes\":0}");
            assertThat(changed.getStatusCode()).isEqualTo(HttpStatus.OK);
            assertThat(changed.getBody().get("maxTextLength").asInt()).isEqualTo(10);

            config = authed(HttpMethod.GET, "/api/v1/client-config",
                    auth.accessToken(), null).getBody();
            assertThat(config.get("limits").get("maxTextLength").asInt()).isEqualTo(10);

            String favorites = favoritesChatId(auth);
            String tooLong = "x".repeat(11);
            ResponseEntity<JsonNode> rejected = authed(HttpMethod.POST,
                    "/api/v1/chats/" + favorites + "/messages", auth.accessToken(),
                    "{\"text\":\"" + tooLong + "\"}");
            assertThat(rejected.getStatusCode()).isEqualTo(HttpStatus.UNPROCESSABLE_ENTITY);

            assertThat(authed(HttpMethod.POST, "/api/v1/chats/" + favorites + "/messages",
                    auth.accessToken(), "{\"text\":\"" + "x".repeat(10) + "\"}")
                    .getStatusCode()).isEqualTo(HttpStatus.CREATED);
        } finally {
            adminPost("/api/v1/admin/limits", "{\"maxTextLength\":4000}");
        }
    }

    @Test
    void pollsCreateVoteAndForwardWithHumanPreview() {
        AuthData alice = tokenLogin(issueToken());
        AuthData bob = tokenLogin(issueToken());
        String chatId = createDirectChat(alice.accessToken(), bob.username());

        ResponseEntity<JsonNode> created = authed(HttpMethod.POST,
                "/api/v1/chats/" + chatId + "/polls", alice.accessToken(),
                "{\"question\":\"Where?\",\"options\":[\"Park\",\"Home\"]}");
        assertThat(created.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        JsonNode poll = created.getBody();
        String pollId = poll.get("id").asText();
        assertThat(poll.get("question").asText()).isEqualTo("Where?");
        assertThat(poll.get("options").size()).isEqualTo(2);
        assertThat(poll.get("totalVotes").asInt()).isZero();
        assertThat(poll.get("closed").asBoolean()).isFalse();
        assertThat(poll.get("myVoteOptionId").isNull()).isTrue();

        String firstOption = poll.get("options").get(0).get("id").asText();
        String secondOption = poll.get("options").get(1).get("id").asText();
        String votePath = "/api/v1/chats/" + chatId + "/polls/" + pollId + "/vote";

        ResponseEntity<JsonNode> voted = authed(HttpMethod.POST, votePath, bob.accessToken(),
                "{\"optionId\":\"" + firstOption + "\"}");
        assertThat(voted.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(voted.getBody().get("totalVotes").asInt()).isEqualTo(1);
        assertThat(voted.getBody().get("myVoteOptionId").asText()).isEqualTo(firstOption);
        assertThat(voted.getBody().get("options").get(0).get("votes").asInt()).isEqualTo(1);

        ResponseEntity<JsonNode> changed = authed(HttpMethod.POST, votePath, bob.accessToken(),
                "{\"optionId\":\"" + secondOption + "\"}");
        assertThat(changed.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(changed.getBody().get("totalVotes").asInt()).isEqualTo(1);
        assertThat(changed.getBody().get("myVoteOptionId").asText()).isEqualTo(secondOption);

        JsonNode read = authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/polls/" + pollId,
                alice.accessToken(), null).getBody();
        assertThat(read.get("totalVotes").asInt()).isEqualTo(1);
        assertThat(read.get("myVoteOptionId").isNull()).isTrue();

        JsonNode messages = authed(HttpMethod.GET, "/api/v1/chats/" + chatId + "/messages",
                alice.accessToken(), null).getBody().get("items");
        assertThat(messages.size()).isEqualTo(1);
        JsonNode card = messages.get(0);
        assertThat(card.get("kind").asText()).isEqualTo("poll");
        assertThat(card.get("text").asText()).contains(pollId).contains("Where?").contains(firstOption);

        String favorites = favoritesChatId(alice);
        ResponseEntity<JsonNode> forwarded = authed(HttpMethod.POST,
                "/api/v1/chats/" + favorites + "/messages/forward", alice.accessToken(),
                "{\"fromChatId\":\"" + chatId + "\",\"messageId\":\"" + card.get("id").asText() + "\"}");
        assertThat(forwarded.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        assertThat(forwarded.getBody().get("kind").asText()).isEqualTo("poll");
        assertThat(forwarded.getBody().get("forwardedFromText").asText()).isEqualTo("Poll: Where?");
    }

    @Test
    void mediaUploadStoresEncryptedMessageAndServesFile() throws Exception {
        AuthData auth = tokenLogin(issueToken());
        String favorites = favoritesChatId(auth);

        MultiValueMap<String, Object> form = new LinkedMultiValueMap<>();
        form.add("file", new ByteArrayResource(PNG) {
            @Override
            public String getFilename() {
                return "photo.png";
            }
        });
        form.add("kind", "photo");
        HttpHeaders headers = new HttpHeaders();
        headers.setBearerAuth(auth.accessToken());
        headers.setContentType(MediaType.MULTIPART_FORM_DATA);
        ResponseEntity<JsonNode> uploaded = rest.exchange(
                "/api/v1/chats/" + favorites + "/messages/media", HttpMethod.POST,
                new HttpEntity<>(form, headers), JsonNode.class);

        assertThat(uploaded.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        JsonNode message = uploaded.getBody();
        assertThat(message.get("kind").asText()).isEqualTo("photo");
        JsonNode payload = new ObjectMapper().readTree(message.get("text").asText());
        String url = payload.get("url").asText();
        assertThat(url).startsWith("/media/media/").endsWith(".png");
        assertThat(payload.get("fileName").asText()).isEqualTo("photo.png");
        assertThat(payload.get("mime").asText()).isEqualTo("image/png");

        ResponseEntity<byte[]> served = rest.getForEntity(url, byte[].class);
        assertThat(served.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(served.getBody()).isEqualTo(PNG);

        MultiValueMap<String, Object> unsupported = new LinkedMultiValueMap<>();
        unsupported.add("file", new ByteArrayResource("bad".getBytes()) {
            @Override
            public String getFilename() {
                return "virus.exe";
            }
        });
        unsupported.add("kind", "file");
        ResponseEntity<JsonNode> rejected = rest.exchange(
                "/api/v1/chats/" + favorites + "/messages/media", HttpMethod.POST,
                new HttpEntity<>(unsupported, headers), JsonNode.class);
        assertThat(rejected.getStatusCode()).isEqualTo(HttpStatus.UNSUPPORTED_MEDIA_TYPE);
    }

    @Test
    void adminLogsRequireSecretAndReturnRecentLines() {
        ResponseEntity<JsonNode> missing = rest.getForEntity("/api/v1/admin/logs?lines=5", JsonNode.class);
        assertThat(missing.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);

        HttpHeaders headers = new HttpHeaders();
        headers.set("X-Admin-Secret", ADMIN_SECRET);
        ResponseEntity<JsonNode> response = rest.exchange("/api/v1/admin/logs?lines=5",
                HttpMethod.GET, new HttpEntity<>(headers), JsonNode.class);
        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        JsonNode lines = response.getBody().get("lines");
        assertThat(lines.isArray()).isTrue();
        assertThat(lines.size()).isGreaterThan(0);
        assertThat(lines.get(lines.size() - 1).asText()).contains("http request");
    }
}
