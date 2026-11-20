package com.aerisflow.messenger.web;

import com.aerisflow.messenger.security.RequestContext;
import com.aerisflow.messenger.service.AuthService;
import com.aerisflow.messenger.web.dto.ApiDtos;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

@RestController
@RequestMapping("/api/v1/auth")
public class AuthController {

    private final AuthService auth;
    private final DtoMapper mapper;

    public AuthController(AuthService auth, DtoMapper mapper) {
        this.auth = auth;
        this.mapper = mapper;
    }

    @PostMapping("/register")
    @ResponseStatus(HttpStatus.CREATED)
    public ApiDtos.AuthResponse register(@RequestBody ApiDtos.RegisterRequest request,
                                         HttpServletRequest httpRequest) {
        AuthService.AuthResult result = auth.register(
                new AuthService.RegisterInput(
                        request.email(),
                        request.username(),
                        request.password(),
                        request.nickname()
                ),
                meta(httpRequest)
        );
        return mapper.auth(result);
    }

    @PostMapping("/login")
    public ApiDtos.AuthResponse login(@RequestBody ApiDtos.LoginRequest request,
                                      HttpServletRequest httpRequest) {
        AuthService.AuthResult result = auth.login(
                new AuthService.LoginInput(request.login(), request.password()),
                meta(httpRequest)
        );
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

    private AuthService.SessionMeta meta(HttpServletRequest request) {
        return new AuthService.SessionMeta(
                request.getHeader("User-Agent"),
                RequestContext.clientIp(request)
        );
    }
}
