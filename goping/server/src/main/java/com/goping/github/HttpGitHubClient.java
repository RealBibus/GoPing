package com.goping.github;

import com.fasterxml.jackson.annotation.JsonProperty;
import com.goping.config.AppProperties;
import com.goping.error.ApiException;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpHeaders;
import org.springframework.http.MediaType;
import org.springframework.stereotype.Service;
import org.springframework.util.LinkedMultiValueMap;
import org.springframework.util.MultiValueMap;
import org.springframework.web.client.RestClient;
import org.springframework.web.client.RestClientException;

@Service
public class HttpGitHubClient implements GitHubClient {

    private static final Logger log = LoggerFactory.getLogger(HttpGitHubClient.class);

    private final AppProperties properties;
    private final RestClient restClient;

    public HttpGitHubClient(AppProperties properties) {
        this.properties = properties;
        this.restClient = RestClient.create();
    }

    @Override
    public Profile authenticate(String code) {
        if (!properties.githubConfigured()) {
            throw ApiException.githubNotConfigured();
        }
        if (code == null || code.isBlank()) {
            throw ApiException.githubAuthFailed();
        }

        String accessToken = exchangeCode(code);
        UserResponse user = fetchUser(accessToken);
        if (user == null || user.id() <= 0 || user.login() == null || user.login().isBlank()) {
            throw ApiException.githubAuthFailed();
        }
        return new Profile(user.id(), user.login(), user.name(), user.avatarUrl());
    }

    private String exchangeCode(String code) {
        MultiValueMap<String, String> form = new LinkedMultiValueMap<>();
        form.add("client_id", properties.githubClientId());
        form.add("client_secret", properties.githubClientSecret());
        form.add("code", code);
        form.add("redirect_uri", properties.githubCallbackUrl());

        try {
            TokenResponse response = restClient.post()
                    .uri("https://github.com/login/oauth/access_token")
                    .contentType(MediaType.APPLICATION_FORM_URLENCODED)
                    .accept(MediaType.APPLICATION_JSON)
                    .body(form)
                    .retrieve()
                    .body(TokenResponse.class);

            if (response == null || response.accessToken() == null || response.accessToken().isBlank()) {
                log.warn("GitHub token exchange failed: {}",
                        response == null ? "empty response" : response.errorDescription());
                throw ApiException.githubAuthFailed();
            }
            return response.accessToken();
        } catch (RestClientException e) {
            log.warn("GitHub token exchange request failed", e);
            throw ApiException.githubAuthFailed();
        }
    }

    private UserResponse fetchUser(String accessToken) {
        try {
            return restClient.get()
                    .uri("https://api.github.com/user")
                    .header(HttpHeaders.AUTHORIZATION, "Bearer " + accessToken)
                    .header(HttpHeaders.ACCEPT, "application/vnd.github+json")
                    .header(HttpHeaders.USER_AGENT, "GoPing")
                    .retrieve()
                    .body(UserResponse.class);
        } catch (RestClientException e) {
            log.warn("GitHub user request failed", e);
            throw ApiException.githubAuthFailed();
        }
    }

    private record TokenResponse(
            @JsonProperty("access_token") String accessToken,
            String error,
            @JsonProperty("error_description") String errorDescription
    ) {
    }

    private record UserResponse(
            long id,
            String login,
            String name,
            @JsonProperty("avatar_url") String avatarUrl
    ) {
    }
}
