#extension GL_OES_EGL_image_external: require
precision highp float;
uniform samplerExternalOES sTexture;
varying mediump float text_alpha_out;
varying highp vec2 vTextureCoord;
varying highp vec2 vTextureCoord2;
void main() {
    vec4 color1 = texture2D(sTexture, vTextureCoord);
    vec4 color2 = texture2D(sTexture, vTextureCoord2);
    gl_FragColor = vec4(color1.rgb, color2.r);
}
