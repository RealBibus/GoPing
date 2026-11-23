package com.goping.config;

import com.goping.media.MediaStore;
import org.springframework.context.annotation.Configuration;
import org.springframework.http.CacheControl;
import org.springframework.web.servlet.config.annotation.CorsRegistry;
import org.springframework.web.servlet.config.annotation.ResourceHandlerRegistry;
import org.springframework.web.servlet.config.annotation.WebMvcConfigurer;

import java.time.Duration;

@Configuration
public class WebConfig implements WebMvcConfigurer {

    private final AppProperties properties;
    private final MediaStore mediaStore;

    public WebConfig(AppProperties properties, MediaStore mediaStore) {
        this.properties = properties;
        this.mediaStore = mediaStore;
    }

    @Override
    public void addCorsMappings(CorsRegistry registry) {
        registry.addMapping("/**")
                .allowedOriginPatterns(properties.corsOrigins().toArray(String[]::new))
                .allowedMethods("GET", "POST", "PATCH", "DELETE", "OPTIONS")
                .allowedHeaders("Authorization", "Content-Type", "X-Request-ID", "X-Admin-Secret")
                .exposedHeaders("X-Request-ID")
                .maxAge(600);
    }

    @Override
    public void addResourceHandlers(ResourceHandlerRegistry registry) {
        registry.addResourceHandler("/media/**")
                .addResourceLocations(mediaStore.resourceLocation())
                .setCacheControl(CacheControl.maxAge(Duration.ofDays(1)).cachePublic());
    }
}
