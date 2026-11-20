package com.goping.web;

import com.goping.error.ApiException;
import com.goping.github.GitHubLoginService;
import com.goping.security.RequestContext;
import com.goping.service.AuthService;
import com.goping.web.dto.ApiDtos;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.util.HtmlUtils;

@RestController
@RequestMapping("/api/v1/auth")
public class AuthController {

    private final AuthService auth;
    private final GitHubLoginService github;
    private final DtoMapper mapper;

    public AuthController(AuthService auth, GitHubLoginService github, DtoMapper mapper) {
        this.auth = auth;
        this.github = github;
        this.mapper = mapper;
    }

    /** Вход по серверному токену. Первый вызов активирует токен. */
    @PostMapping("/token")
    public ApiDtos.AuthResponse token(@RequestBody ApiDtos.ServerTokenLoginRequest request,
                                      HttpServletRequest httpRequest) {
        AuthService.AuthResult result = auth.loginWithServerToken(request.token(), meta(httpRequest));
        return mapper.auth(result);
    }

    @PostMapping("/refresh")
    public ApiDtos.AuthResponse refresh(@RequestBody ApiDtos.RefreshRequest request,
                                        HttpServletRequest httpRequest) {
        AuthService.AuthResult result = auth.refresh(request.refreshToken(), meta(httpRequest));
        return mapper.auth(result);
    }

    @PostMapping("/logout")
    @ResponseStatus(HttpStatus.NO_CONTENT)
    public void logout(@RequestBody ApiDtos.RefreshRequest request) {
        auth.logout(request.refreshToken());
    }

    /** Начинает браузерный вход через GitHub. */
    @GetMapping("/github/start")
    public ApiDtos.GithubStartResponse githubStart() {
        GitHubLoginService.Start start = github.start();
        return new ApiDtos.GithubStartResponse(start.authorizeUrl(), start.state(), start.expiresAt());
    }

    /** Callback GitHub. Токены не выдаёт: клиент забирает их через poll. */
    @GetMapping(value = "/github/callback", produces = MediaType.TEXT_HTML_VALUE)
    public String githubCallback(@RequestParam(required = false) String code,
                                 @RequestParam(required = false) String state,
                                 @RequestParam(required = false) String error) {
        try {
            github.callback(code, state, error);
            return page(true, "GitHub подключён. Вернитесь в GoPing — вход завершится автоматически.");
        } catch (ApiException e) {
            return page(false, e.getMessage());
        }
    }

    /** Клиент опрашивает этот endpoint, пока пользователь подтверждает вход. */
    @GetMapping("/github/poll")
    public ApiDtos.GithubPollResponse githubPoll(@RequestParam String state,
                                                 HttpServletRequest httpRequest) {
        GitHubLoginService.Poll poll = github.poll(state, meta(httpRequest));
        if (!poll.ready()) {
            return new ApiDtos.GithubPollResponse("pending", null);
        }
        return new ApiDtos.GithubPollResponse("ready", mapper.auth(poll.result()));
    }

    private AuthService.SessionMeta meta(HttpServletRequest request) {
        return new AuthService.SessionMeta(
                request.getHeader("User-Agent"),
                RequestContext.clientIp(request)
        );
    }

    private String page(boolean success, String message) {
        String safe = HtmlUtils.htmlEscape(message == null ? "" : message);
        String color = success ? "#16A34A" : "#DC2626";
        String title = success ? "Готово" : "Не удалось войти";
        return """
                <!doctype html>
                <html lang="ru">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width,initial-scale=1">
                  <title>GoPing</title>
                  <style>
                    body{margin:0;min-height:100vh;display:grid;place-items:center;background:#fafafa;
                         color:#18181b;font:15px/1.5 "Segoe UI",system-ui,sans-serif}
                    main{max-width:440px;padding:40px;background:#fff;border:1px solid #e5e5e5;border-radius:12px}
                    .dot{width:10px;height:10px;border-radius:50%%;background:%s;display:inline-block;margin-right:8px}
                    h1{font-size:20px;margin:0 0 10px}
                    p{margin:0;color:#52525b}
                    small{display:block;margin-top:22px;color:#a1a1aa}
                  </style>
                </head>
                <body>
                  <main>
                    <h1><span class="dot"></span>%s</h1>
                    <p>%s</p>
                    <small>GoPing — a local messenger for you and your friends.</small>
                  </main>
                </body>
                </html>
                """.formatted(color, title, safe);
    }
}
