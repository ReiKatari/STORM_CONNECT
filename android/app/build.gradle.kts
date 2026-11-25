import java.io.FileInputStream
import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val stormSigningProps = Properties().apply {
    val propsFile = file("F:/STORM ALL PROJECTS/.storm-secrets/storm_signing.properties")
    if (propsFile.exists()) {
        FileInputStream(propsFile).use { load(it) }
    }
}

android {
    namespace = "com.storm.connect"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.storm.connect"
        minSdk = 26
        targetSdk = 36
        versionCode = 131
        versionName = "1.3.1"
    }

    signingConfigs {
        create("stormUnifiedRelease") {
            val certPath = stormSigningProps.getProperty("STORM_CERT_FILE") ?: System.getenv("STORM_CERT_FILE")
            if (!certPath.isNullOrBlank() && file(certPath).exists()) {
                storeFile = file(certPath)
                storePassword = stormSigningProps.getProperty("STORM_CERT_PASSWORD") ?: System.getenv("STORM_CERT_PASSWORD")
                keyAlias = stormSigningProps.getProperty("STORM_KEY_ALIAS") ?: System.getenv("STORM_KEY_ALIAS")
                keyPassword = stormSigningProps.getProperty("STORM_KEY_PASSWORD") ?: System.getenv("STORM_KEY_PASSWORD")
                enableV1Signing = true
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("stormUnifiedRelease")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
}
