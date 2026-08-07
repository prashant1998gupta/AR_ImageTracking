<?php
/**
 * AR Analytics — Main API Router
 * All requests are routed through this file via .htaccess
 */

require_once __DIR__ . '/helpers/Response.php';

Response::cors();
Response::json();

// Parse the request path
$requestUri = $_SERVER['REQUEST_URI'];
$basePath = dirname($_SERVER['SCRIPT_NAME']);
$path = trim(str_replace($basePath, '', parse_url($requestUri, PHP_URL_PATH)), '/');

// Route to appropriate controller
$segments = explode('/', $path);
$controller = $segments[0] ?? '';

// Map URL actions to GET params for controllers
if (isset($segments[1])) {
    $_GET['action'] = $segments[1];
}
if (isset($segments[2])) {
    $_GET['id'] = $segments[2];
}

switch ($controller) {
    case 'track':
        require_once __DIR__ . '/controllers/track.php';
        break;
    case 'auth':
        require_once __DIR__ . '/controllers/auth.php';
        break;
    case 'dashboard':
        require_once __DIR__ . '/controllers/dashboard.php';
        break;
    case 'admin':
        require_once __DIR__ . '/controllers/admin.php';
        break;
    case 'hunt':
        require_once __DIR__ . '/controllers/hunt.php';
        break;
    default:
        Response::success([
            'name' => 'AR Analytics API',
            'version' => '1.0.0',
            'status' => 'running',
        ]);
}
