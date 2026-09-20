# ProGuard rules for the TFTF PvP Host. The server core uses no reflection, so
# the rule set is deliberately narrow (no blanket app-package keep).

# Manifest components are resolved by name.
-keep public class com.gummygamer.tftfpvphost.MainActivity
-keep public class com.gummygamer.tftfpvphost.HostService

# View binding classes are resolved from the layout file names.
-keep class com.gummygamer.tftfpvphost.databinding.** { *; }
-keepclassmembers class com.gummygamer.tftfpvphost.databinding.** {
    public static *** bind(android.view.View);
    public static *** inflate(android.view.LayoutInflater);
}

# AndroidX lifecycle instantiates ViewModels reflectively.
-keepclassmembers class * extends androidx.lifecycle.ViewModel { <init>(...); }
-dontwarn androidx.lifecycle.**

-dontwarn kotlinx.coroutines.**
-dontwarn org.jetbrains.annotations.**
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile
-dontnote kotlinx.**
-dontnote androidx.**
-dontnote kotlin.**
