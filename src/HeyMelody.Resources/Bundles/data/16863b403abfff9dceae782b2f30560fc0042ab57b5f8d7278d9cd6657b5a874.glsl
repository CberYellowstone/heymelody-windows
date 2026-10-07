uniform mat4 uMVPMatrix;
uniform mat4 uSTMatrix;
attribute vec4 aPosition;
attribute vec4 aTextureCoord;
varying highp vec2 vTextureCoord;
varying highp vec2 vTextureCoord2;
void main() {
  gl_Position = uMVPMatrix * aPosition;
  highp vec2 coord = (uSTMatrix * aTextureCoord).xy;
  vTextureCoord = vec2(coord.x * 0.5, coord.y);
  vTextureCoord2 = vec2(coord.x * 0.5 + 0.5, coord.y);
}