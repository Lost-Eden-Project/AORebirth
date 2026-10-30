<?php
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

// HTTP supplies routing hints only. Authenticated native action 263 owns every
// grant and progress change; this endpoint never trusts a hint as an account login.
function daily_safe_key($key) {
    return preg_replace('/[^a-z0-9_\-]+/', '_', strtolower(trim(strval($key))));
}

function daily_character_hint($value, $required = false) {
    if (!is_int($value) && !is_string($value)) {
        throw new InvalidArgumentException('Invalid daily reward character routing hint.');
    }
    $text = strval($value);
    if (!$required && ($text === '' || $text === '0')) return 0;
    if (!preg_match('/^[1-9][0-9]{0,9}$/D', $text) || strlen($text) > 10 ||
        (strlen($text) === 10 && strcmp($text, '2147483647') > 0)) {
        throw new InvalidArgumentException('Invalid daily reward character routing hint.');
    }
    return intval($text);
}

function daily_claim_roots() {
    $configured = getenv('AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS');
    if ($configured !== false && trim($configured) !== '') {
        $roots = array_filter(array_map('trim', explode(PATH_SEPARATOR, $configured)), 'strlen');
        if (count($roots) === 0) {
            throw new RuntimeException('Daily reward claim roots are not configured.');
        }
        return array_values(array_unique($roots));
    }
    $roots = array(__DIR__ . DIRECTORY_SEPARATOR . 'data' . DIRECTORY_SEPARATOR . 'claims');
    if (DIRECTORY_SEPARATOR === '\\') {
        $roots[] = 'C:\\xampp\\htdocs\\uwg.daily.icc-rk\\data\\claims';
        $roots[] = 'C:\\xampp\\htdocs\\daily\\data\\claims';
    }
    return $roots;
}

function daily_read_consistent($relativeName, $characterId = 0, $ignoreUnattributed = false) {
    $found = null;
    foreach (daily_claim_roots() as $root) {
        $path = $root . DIRECTORY_SEPARATOR . $relativeName;
        if (!is_file($path)) {
            continue;
        }
        $raw = @file_get_contents($path);
        $value = $raw === false ? null : json_decode($raw, true);
        if (!is_array($value)) {
            throw new RuntimeException('Daily reward state could not be read. Existing files were preserved.');
        }
        if ($characterId > 0) {
            $owner = daily_result_character($value);
            if ($owner === 0 && $ignoreUnattributed) continue;
            if ($owner !== $characterId) {
                throw new RuntimeException('Daily reward projection belongs to a different character. Existing files were preserved.');
            }
        }
        if ($found !== null && $found != $value) {
            throw new RuntimeException('Daily reward state differs between claim roots. Existing files were preserved.');
        }
        $found = $value;
    }
    return $found;
}

function daily_token($value) {
    $token = strval($value);
    if ($token !== '' && !preg_match('/^[a-zA-Z0-9_\-]{1,64}$/', $token)) {
        throw new InvalidArgumentException('Invalid daily reward claim token.');
    }
    return $token;
}

function daily_pending_name($token) {
    return 'request-' . daily_safe_key($token) . '.json';
}

function daily_result_character($result) {
    return isset($result['characterId']) ? daily_character_hint($result['characterId'], true) : 0;
}

function daily_character_result($token, $characterId, $accountKey) {
    $result = daily_read_consistent('result-character-' . $characterId . '-' . daily_safe_key($token) . '.json', $characterId);
    if ($result !== null) {
        if (!isset($result['claimToken']) || $result['claimToken'] !== $token ||
            ($accountKey !== '' && (!isset($result['accountKey']) || daily_safe_key($result['accountKey']) !== $accountKey))) {
            throw new RuntimeException('Daily reward receipt ownership does not match this request. The original receipt was preserved.');
        }
        return $result;
    }
    return null;
}

function daily_legacy_result_exists($token) {
    foreach (daily_claim_roots() as $root) {
        if (is_file($root . DIRECTORY_SEPARATOR . 'result-' . daily_safe_key($token) . '.json')) return true;
    }
    return false;
}

function daily_same_request($first, $second) {
    foreach (array('ClaimToken', 'CharacterId') as $key) {
        if (!array_key_exists($key, $first) || $first[$key] !== $second[$key]) {
            return false;
        }
    }
    return $first['CharacterId'] > 0 || (isset($first['AccountKey']) && $first['AccountKey'] === $second['AccountKey']);
}

function daily_write_request($request) {
    $relativeName = daily_pending_name($request['ClaimToken']);
    $existing = daily_read_consistent($relativeName);
    if ($existing !== null) {
        if (!daily_same_request($existing, $request)) {
            throw new RuntimeException('This claim token already belongs to a different request. The original request was preserved.');
        }
        $request = $existing;
    }
    $json = json_encode($request, JSON_UNESCAPED_SLASHES);
    if ($json === false) {
        throw new RuntimeException('Daily reward request could not be encoded.');
    }
    foreach (daily_claim_roots() as $root) {
        if (!is_dir($root) && !@mkdir($root, 0770, true) && !is_dir($root)) {
            throw new RuntimeException('Daily reward queue is unavailable. Retry the saved claim token.');
        }
        $path = $root . DIRECTORY_SEPARATOR . $relativeName;
        $lock = @fopen($root . DIRECTORY_SEPARATOR . 'request-write.lock', 'c');
        if ($lock === false || !flock($lock, LOCK_EX)) {
            if (is_resource($lock)) fclose($lock);
            throw new RuntimeException('Daily reward queue could not be locked. Retry the saved claim token.');
        }
        $temporary = null;
        try {
            if (is_file($path)) {
                $value = json_decode(@file_get_contents($path), true);
                if (!is_array($value) || !daily_same_request($value, $request)) {
                    throw new RuntimeException('A conflicting daily reward request was preserved.');
                }
                $request = $value;
                $json = json_encode($request, JSON_UNESCAPED_SLASHES);
                continue;
            }
            $temporary = @tempnam($root, '.daily-request-');
            if ($temporary === false || realpath(dirname($temporary)) !== realpath($root)) {
                throw new RuntimeException('Daily reward request was not saved. Retry the saved claim token.');
            }
            $output = @fopen($temporary, 'wb');
            if ($output === false) {
                throw new RuntimeException('Daily reward request could not be written. Retry the saved claim token.');
            }
            try {
                if (fwrite($output, $json) !== strlen($json) || !fflush($output) || (function_exists('fsync') && !fsync($output))) {
                    throw new RuntimeException('Daily reward request was not saved. Retry the saved claim token.');
                }
            } finally {
                fclose($output);
            }
            if (!@rename($temporary, $path)) {
                throw new RuntimeException('Daily reward request was not published. Retry the saved claim token.');
            }
            $temporary = null;
        } finally {
            if ($temporary !== null && is_file($temporary)) @unlink($temporary);
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
    return $request;
}

function daily_project_board($board) {
    $taken = isset($board['taken']) && is_array($board['taken']) ? array_map('intval', $board['taken']) : array();
    foreach ($taken as $day) {
        if ($day < 1 || $day > 28) {
            throw new RuntimeException('Daily reward progress contains an invalid taken day. Existing files were preserved.');
        }
    }
    $taken = array_values(array_unique($taken));
    sort($taken);
    $claimed = isset($board['lastClaimUtc']) ? strval($board['lastClaimUtc']) : '';
    $granted = isset($board['lastGrantedUtc']) ? strval($board['lastGrantedUtc']) : '';
    $completed = isset($board['cycleCompletedOn']) ? strval($board['cycleCompletedOn']) : '';
    foreach (array($claimed, $granted, $completed) as $date) {
        if ($date === '') continue;
        if (!preg_match('/^[0-9]{4}-[0-9]{2}-[0-9]{2}$/', $date)) {
            throw new RuntimeException('Daily reward progress contains an invalid UTC date. Existing files were preserved.');
        }
        $parts = array_map('intval', explode('-', $date));
        if (!checkdate($parts[1], $parts[2], $parts[0])) {
            throw new RuntimeException('Daily reward progress contains an invalid UTC date. Existing files were preserved.');
        }
    }
    if (count($taken) > 0 && $claimed === '' && $granted === '') {
        throw new RuntimeException('Daily reward progress has taken days without a grant date. Existing files were preserved.');
    }
    // Read-only projection of DailyLoginAccountState.Normalize: incomplete
    // cycles survive month changes; only a completed cycle resets tomorrow UTC.
    if ($granted === '' && count($taken) > 0) $granted = $claimed;
    if (count($taken) === 28 && $completed === '') $completed = $granted !== '' ? $granted : $claimed;
    $today = gmdate('Y-m-d');
    if (count($taken) === 28 && $completed !== '' && strcmp($today, $completed) > 0) {
        $taken = array();
        $claimed = $granted = $completed = '';
    }
    $next = 0;
    for ($day = 1; $day <= 28; $day++) {
        if (!in_array($day, $taken, true)) {
            $next = $day;
            break;
        }
    }
    $board['month'] = gmdate('Y-m');
    $board['taken'] = $taken;
    $board['claimedCount'] = count($taken);
    $board['nextDay'] = $next;
    $board['lastClaimUtc'] = $claimed;
    $board['lastGrantedUtc'] = $granted;
    $board['cycleCompletedOn'] = $completed;
    $board['claimedToday'] = $granted !== '' && strcmp($granted, $today) >= 0;
    return $board;
}

function daily_board($characterId, $accountKey) {
    $board = $characterId > 0 ? daily_read_consistent('board-character-' . $characterId . '.json', $characterId, true) : null;
    // Old files under this name were account projections. Retain them as history,
    // but only an explicitly character-owned server projection may supply Taken.
    if ($board !== null) {
        $owner = daily_result_character($board);
        if ($owner > 0 && $owner !== $characterId) {
            throw new RuntimeException('Daily reward board belongs to a different character. Existing progress was preserved.');
        }
        if ($owner === 0) $board = null;
    }
    if ($board === null || empty($board['accountKey'])) {
        $hasHint = $characterId > 0;
        return array(
            'ok' => true, 'hasIdentity' => $hasHint, 'characterId' => $characterId,
            'accountKey' => '', 'progressVerified' => false,
            'taken' => array(), 'claimedCount' => 0, 'lastClaimUtc' => '', 'lastGrantedUtc' => '',
            'message' => $hasHint ? 'The game server will verify this character when the native claim action arrives.' : 'This browser has no character routing hint. Open Daily Rewards from the logged-in character.'
        );
    }
    $board['ok'] = true;
    $board['hasIdentity'] = true;
    $board['progressVerified'] = true;
    return daily_project_board($board);
}

try {
    $method = isset($_SERVER['REQUEST_METHOD']) ? $_SERVER['REQUEST_METHOD'] : 'GET';
    if ($method !== 'GET' && $method !== 'POST') {
        http_response_code(405);
        echo json_encode(array('ok' => false, 'message' => 'Use GET or POST for daily rewards.'));
        exit;
    }
    $body = array();
    if ($method === 'POST') {
        $decoded = json_decode(file_get_contents('php://input'), true);
        if (!is_array($decoded)) {
            throw new InvalidArgumentException('Invalid daily reward request.');
        }
        $body = $decoded;
    }
    $bodyCharacterId = isset($body['characterId']) ? daily_character_hint($body['characterId']) : 0;
    $queryCharacterId = isset($_GET['characterId']) ? daily_character_hint($_GET['characterId']) : 0;
    // The native browser supplies this per-request header. It is only a route;
    // the authenticated game session still owns every grant and account decision.
    $nativeCharacterId = isset($_SERVER['HTTP_X_ANARCHY_CHARACTERID'])
        ? daily_character_hint($_SERVER['HTTP_X_ANARCHY_CHARACTERID'], true) : 0;
    if (($bodyCharacterId > 0 && $queryCharacterId > 0 && $bodyCharacterId !== $queryCharacterId) ||
        ($nativeCharacterId > 0 && (($bodyCharacterId > 0 && $bodyCharacterId !== $nativeCharacterId) ||
            ($queryCharacterId > 0 && $queryCharacterId !== $nativeCharacterId)))) {
        throw new InvalidArgumentException('Daily reward character routing hints conflict. The saved request was not changed.');
    }
    $characterId = $nativeCharacterId > 0 ? $nativeCharacterId : ($bodyCharacterId > 0 ? $bodyCharacterId : $queryCharacterId);
    $accountKey = isset($body['accountKey']) ? daily_safe_key($body['accountKey']) : (isset($_GET['accountKey']) ? daily_safe_key($_GET['accountKey']) : '');

    if ($method === 'GET') {
        $token = isset($_GET['token']) ? daily_token($_GET['token']) : '';
        if ($token !== '') {
            if ($characterId <= 0) {
                throw new InvalidArgumentException('A character routing hint is required to read a daily reward receipt.');
            }
            $result = daily_character_result($token, $characterId, $accountKey);
            $unattributed = $result === null && daily_legacy_result_exists($token);
            if ($result !== null && !$unattributed) {
                if (!empty($result['ok']) && !array_key_exists('committed', $result) && isset($result['day']) && intval($result['day']) > 0) {
                    // Preserve the original receipt file. Its earlier success must
                    // never be replayed as a new request or atomic-commit proof.
                    $result['legacyResolved'] = true;
                    $result['terminal'] = true;
                    $result['committed'] = false;
                }
                echo json_encode($result);
                exit;
            }
            $request = daily_read_consistent(daily_pending_name($token));
            if ($request !== null && (!isset($request['CharacterId']) || intval($request['CharacterId']) !== $characterId)) {
                throw new RuntimeException('Daily reward request belongs to a different character. The original request was preserved.');
            }
            echo json_encode(array(
                'ok' => null, 'pending' => true, 'armed' => $request !== null,
                'claimToken' => $token, 'characterId' => $characterId,
                'legacyVerificationRequired' => $unattributed,
                'message' => $unattributed ? 'The original receipt is retained. Waiting for native character verification.' : 'Waiting for the game server to confirm this claim.'
            ));
            exit;
        }
        $board = daily_board($characterId, $accountKey);
        $board['nativeCharacterHint'] = $nativeCharacterId > 0;
        echo json_encode($board);
        exit;
    }

    $token = isset($body['claimToken']) ? daily_token($body['claimToken']) : '';
    if ($token === '') {
        $token = 'c' . bin2hex(random_bytes(24));
    }
    if ($characterId <= 0) {
        echo json_encode(array(
            'ok' => false, 'armed' => false, 'claimToken' => $token,
            'message' => 'This browser has no character routing hint. Reopen Daily Rewards from the logged-in character.'
        ));
        exit;
    }
    $request = daily_write_request(array(
        'Day' => 1, 'ItemId' => 1, 'Amount' => 1, 'Quality' => 1,
        'CharacterId' => $characterId, 'AccountKey' => $accountKey,
        'ClaimToken' => $token, 'RequestedUtc' => gmdate('c')
    ));
    echo json_encode(array(
        'ok' => true, 'armed' => true, 'committed' => false,
        'claimToken' => $request['ClaimToken'], 'accountKey' => $request['AccountKey'],
        'characterId' => $request['CharacterId'],
        'message' => 'Claim queued. Waiting for the game server to confirm delivery.'
    ));
} catch (Exception $error) {
    http_response_code(503);
    echo json_encode(array(
        'ok' => false, 'armed' => false, 'committed' => false,
        'message' => $error->getMessage()
    ));
}
