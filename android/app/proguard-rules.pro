# ProGuard rules for the TFTF APK Patcher.
#
# The release build minifies and shrinks resources. Two things must survive:
# the Bouncy Castle classes the app instantiates for on-device certificate and
# signature work, and the app classes referenced from AndroidManifest.xml or by
# reflection from the security providers.

# Keep the manifest components by name. AndroidManifest references MainActivity
# and InstallResultReceiver, and view binding resolves the generated binding
# classes reflectively from the layout file names.
-keep public class com.gummygamer.apkpatcher.MainActivity
-keep public class com.gummygamer.apkpatcher.InstallResultReceiver
-keep class com.gummygamer.apkpatcher.databinding.** { *; }
-keepclassmembers class com.gummygamer.apkpatcher.databinding.** {
    public static *** bind(android.view.View);
    public static *** inflate(android.view.LayoutInflater);
}

# Bouncy Castle: the app builds its own BouncyCastleProvider instances and the
# library resolves algorithm implementations and ASN.1 helpers through its own
# reflection. Stripping or renaming these classes breaks keystore generation and
# APK Signature Scheme v2 signing on devices whose platform providers are
# incomplete.
-keep class org.bouncycastle.** { *; }
-dontwarn org.bouncycastle.**
-dontwarn org.bouncycastle.jsse.**
# BC is dual-licensed with the JCE provider name registered via resource files.
-keepattributes *Annotation*,Signature,InnerClasses,EnclosingMethod,Exceptions
-keep class * implements java.security.Provider { *; }
-keepnames class * implements java.security.Provider
-dontnote org.bouncycastle.**

# The sun.security.x509 fallback path referenced by unit tests is not present on
# Android runtimes; the app already falls back to Bouncy Castle. Silence the
# resulting warnings instead of failing the release build.
-dontwarn sun.security.**
-dontwarn java.lang.invoke.**
-dontwarn javax.naming.**

# AndroidX/Material/Kotlin coroutines keep their own consumer rules, but the
# coroutine debug metadata and nullability annotations are safe to drop.
-dontwarn kotlinx.coroutines.**
-dontwarn org.jetbrains.annotations.**

# Kotlin metadata is needed by androidx lifecycle reflection over ViewModels.
-keep class androidx.lifecycle.** { *; }
-keepclassmembers class * extends androidx.lifecycle.ViewModel {
    <init>(...);
}
-dontwarn androidx.lifecycle.**

# Keep line numbers for crash reporting and drop noisy notes from the AndroidX
# and Kotlin dependencies.
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile
-dontnote kotlinx.**
-dontnote android.**
-dontnote androidx.**
-dontnote kotlin.**
