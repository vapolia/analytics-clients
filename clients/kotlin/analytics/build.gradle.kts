import com.vanniktech.maven.publish.AndroidSingleVariantLibrary

plugins {
    alias(libs.plugins.android.library)
    alias(libs.plugins.maven.publish)
}

android {
    namespace = "com.vapolia.analytics"
    compileSdk = 36

    defaultConfig {
        minSdk = 24
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
}

dependencies {
    testImplementation(libs.junit)
}

mavenPublishing {
    publishToMavenCentral()

    // Central refuses an unsigned artifact, so signing is on as soon as a key is configured. Without
    // one, `publishToMavenLocal` still works: trying the library in an app must not need a GPG key.
    if (providers.gradleProperty("signingInMemoryKey").isPresent || project.hasProperty("signing.keyId"))
        signAllPublications()

    configure(AndroidSingleVariantLibrary("release", sourcesJar = true, publishJavadocJar = true))

    coordinates(
        groupId = project.property("GROUP") as String,
        artifactId = "analytics",
        version = project.property("VERSION_NAME") as String,
    )

    pom {
        name.set("Vapolia Analytics")
        description.set("Android client for the Vapolia audience-measurement collector.")
        inceptionYear.set("2026")
        url.set("https://github.com/vapolia/analytics-clients")
        // The text itself also ships inside the aar: src/main/resources/META-INF/analytics_LICENSE,
        // named so it cannot clash with another dependency's META-INF/LICENSE on a consumer merge.
        licenses {
            license {
                name.set("MIT License")
                url.set("https://opensource.org/licenses/MIT")
            }
        }
        developers {
            developer {
                id.set("vapolia")
                name.set("Vapolia")
                url.set("https://vapolia.eu")
            }
        }
        scm {
            url.set("https://github.com/vapolia/analytics-clients")
            connection.set("scm:git:git://github.com/vapolia/analytics-clients.git")
            developerConnection.set("scm:git:ssh://git@github.com/vapolia/analytics-clients.git")
        }
    }
}
