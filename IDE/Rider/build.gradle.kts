plugins {
    id("java")
    id("org.jetbrains.kotlin.jvm") version "2.2.20"
    id("org.jetbrains.intellij.platform") version "2.20.1"
}

group = "com.donkeh"
version = "0.0.1"

repositories {
    mavenCentral()

    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        // CI has no local Rider installation, so use the public distribution by
        // default. Set CSX_RIDER_LOCAL (or -Pcsx.rider.local) to build against a
        // local IDE and skip the download.
        val localRider = providers.environmentVariable("CSX_RIDER_LOCAL")
            .orElse(providers.gradleProperty("csx.rider.local"))

        if (localRider.isPresent) {
            local(localRider.get())
        } else {
            val platformVersion = providers.environmentVariable("CSX_RIDER_PLATFORM_VERSION")
                .orElse(providers.gradleProperty("csx.rider.platformVersion"))
                .getOrElse("2026.1")
            rider(platformVersion)
        }
    }

    testImplementation("junit:junit:4.13.2")
}

kotlin {
    jvmToolchain(21)
}

intellijPlatform {
    pluginConfiguration {
        id = "com.donkeh.csx.rider"
        name = "CSX Language Support"
        version = project.version.toString()
        description = "Language support for CSharpExtended (.cx) files in Rider, backed by the CSX language server."
        vendor {
            name = "donkeh"
        }

        ideaVersion {
            sinceBuild = "261"
            untilBuild = provider { null }
        }
    }
}

tasks {
    withType<JavaCompile> {
        options.encoding = "UTF-8"
    }

    named("buildSearchableOptions") {
        enabled = false
    }

    named("prepareJarSearchableOptions") {
        enabled = false
    }

    buildPlugin {
        archiveFileName.set("csx-rider-${project.version}.zip")
    }
}
