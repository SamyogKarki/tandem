plugins {
    id("com.android.application")
}

android {
    namespace = "io.github.samyogkarki.tandem"
    compileSdk = 36

    defaultConfig {
        applicationId = "io.github.samyogkarki.tandem"
        // Wireless debugging (how Tandem connects) exists from Android 11.
        minSdk = 30
        targetSdk = 36
        versionCode = 7
        versionName = "0.3.3"
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            // Installed by the Windows app over adb, never through a store. Until there is a
            // release key, sign with the local debug key so `assembleRelease` works anywhere.
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
